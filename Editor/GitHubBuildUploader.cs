using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using CompressionLevel = System.IO.Compression.CompressionLevel;

namespace BuildUploader.Editor
{
    public class GitHubBuildUploader : IPostprocessBuildWithReport
    {
        public const string EnabledKey = "GitHubBuildUploader.Enabled";
        public const string GitHubTokenKey = "GitHubBuildUploader.GitHubToken";
        private const string GitHubRepoKeyBase = "GitHubBuildUploader.GitHubRepo";

        private const string LegacyEnabledKey = "DiscordBuildUploader.Enabled";
        private const string LegacyTokenKey = "DiscordBuildUploader.GitHubToken";
        private const string LegacyRepoKey = "DiscordBuildUploader.GitHubRepo";

        public static string GitHubRepoKey => GitHubRepoKeyBase + "." + ProjectId;

        private static string ProjectId
        {
            get
            {
                string dataPath = Application.dataPath ?? string.Empty;
                using (var sha = SHA1.Create())
                {
                    byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(dataPath));
                    return BitConverter.ToString(hash, 0, 8).Replace("-", string.Empty);
                }
            }
        }

        public int callbackOrder => 0;

        internal static void MigrateLegacyPrefs()
        {
            if (!EditorPrefs.HasKey(EnabledKey) && EditorPrefs.HasKey(LegacyEnabledKey))
                EditorPrefs.SetBool(EnabledKey, EditorPrefs.GetBool(LegacyEnabledKey, false));
            if (!EditorPrefs.HasKey(GitHubTokenKey) && EditorPrefs.HasKey(LegacyTokenKey))
                EditorPrefs.SetString(GitHubTokenKey, EditorPrefs.GetString(LegacyTokenKey, string.Empty));

            // Repo is per-project. Fall back to the old global key (Discord or GitHub) once so
            // existing setups are carried forward into this project's slot.
            if (!EditorPrefs.HasKey(GitHubRepoKey))
            {
                if (EditorPrefs.HasKey(GitHubRepoKeyBase))
                    EditorPrefs.SetString(GitHubRepoKey, EditorPrefs.GetString(GitHubRepoKeyBase, string.Empty));
                else if (EditorPrefs.HasKey(LegacyRepoKey))
                    EditorPrefs.SetString(GitHubRepoKey, EditorPrefs.GetString(LegacyRepoKey, string.Empty));
            }
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            MigrateLegacyPrefs();

            var result = report.summary.result;
            if (result == BuildResult.Failed || result == BuildResult.Cancelled)
                return;
            if (result == BuildResult.Unknown && string.IsNullOrEmpty(report.summary.outputPath))
                return;

            bool enabled = EditorPrefs.GetBool(EnabledKey, false);
            if (!enabled)
            {
                Debug.Log("[BuildUploader] Uploader disabled; skipping.");
                return;
            }

            string githubToken = EditorPrefs.GetString(GitHubTokenKey, string.Empty)?.Trim();
            string githubRepo = EditorPrefs.GetString(GitHubRepoKey, string.Empty)?.Trim().Trim('/');

            if (string.IsNullOrWhiteSpace(githubToken))
            {
                Debug.Log("[BuildUploader] No GitHub token configured; skipping.");
                return;
            }
            if (string.IsNullOrWhiteSpace(githubRepo) || !githubRepo.Contains("/"))
            {
                Debug.Log("[BuildUploader] GitHub repo must be formatted as 'owner/name'; skipping.");
                return;
            }

            string outputPath = report.summary.outputPath;
            if (string.IsNullOrEmpty(outputPath))
            {
                Debug.LogWarning("[BuildUploader] Build output path is empty; skipping.");
                return;
            }

            string buildDirectory = Directory.Exists(outputPath)
                ? outputPath
                : Path.GetDirectoryName(outputPath);

            if (string.IsNullOrEmpty(buildDirectory) || !Directory.Exists(buildDirectory))
            {
                Debug.LogWarning($"[BuildUploader] Could not resolve build directory from '{outputPath}'; skipping.");
                return;
            }

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string zipName = $"{PlayerSettings.productName}_{report.summary.platform}_{timestamp}.zip";
            string zipPath = Path.Combine(Path.GetTempPath(), zipName);
            string tagName = $"build-{report.summary.platform}-{timestamp}";
            string releaseName = $"{PlayerSettings.productName} {report.summary.platform} ({timestamp})";

            Debug.Log($"[BuildUploader] Packaging and uploading in background: {releaseName}. You can keep using the editor; closing Unity will abort the upload.");

            // Fire-and-forget: zip + upload run on a background thread so the editor stays responsive.
            _ = Task.Run(async () =>
            {
                try
                {
                    if (File.Exists(zipPath))
                        File.Delete(zipPath);

                    ZipFile.CreateFromDirectory(buildDirectory, zipPath, CompressionLevel.Optimal, false);

                    long zipLen = new FileInfo(zipPath).Length;

                    await UploadAsync(githubToken, githubRepo, tagName, releaseName, zipPath, zipLen).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[BuildUploader] Failed to package or upload build: {ex.Message}");
                }
                finally
                {
                    try
                    {
                        if (File.Exists(zipPath))
                            File.Delete(zipPath);
                    }
                    catch (Exception cleanupEx)
                    {
                        Debug.LogWarning($"[BuildUploader] Failed to delete temp zip '{zipPath}': {cleanupEx.Message}");
                    }
                }
            });
        }

        private static async Task UploadAsync(
            string token,
            string repo,
            string tagName,
            string releaseName,
            string zipPath,
            long zipBytes)
        {
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("UnityBuildUploader/1.0");
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                client.Timeout = TimeSpan.FromMinutes(30);

                string createBody =
                    "{"
                    + $"\"tag_name\":\"{JsonEscape(tagName)}\","
                    + $"\"name\":\"{JsonEscape(releaseName)}\","
                    + "\"body\":\"Automated build from Unity editor.\","
                    + "\"draft\":false,"
                    + "\"prerelease\":true"
                    + "}";

                string releasesUrl = $"https://api.github.com/repos/{repo}/releases";
                HttpResponseMessage createResp;
                try
                {
                    createResp = await client.PostAsync(releasesUrl, new StringContent(createBody, Encoding.UTF8, "application/json")).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[BuildUploader] HTTP error creating GitHub release: {ex.Message}");
                    return;
                }

                if (!createResp.IsSuccessStatusCode)
                {
                    string err = await createResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    Debug.LogError($"[BuildUploader] Failed to create GitHub release: {(int)createResp.StatusCode} {createResp.ReasonPhrase} — {err}");
                    return;
                }

                string createJson = await createResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                string uploadUrl = ExtractJsonString(createJson, "upload_url");
                string htmlUrl = ExtractJsonString(createJson, "html_url");

                if (string.IsNullOrEmpty(uploadUrl))
                {
                    Debug.LogError("[BuildUploader] GitHub release created but response had no upload_url.");
                    return;
                }

                int brace = uploadUrl.IndexOf('{');
                if (brace >= 0)
                    uploadUrl = uploadUrl.Substring(0, brace);
                uploadUrl += $"?name={Uri.EscapeDataString(Path.GetFileName(zipPath))}";

                string downloadUrl;
                using (var fs = File.OpenRead(zipPath))
                {
                    var assetContent = new StreamContent(fs);
                    assetContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

                    HttpResponseMessage assetResp;
                    try
                    {
                        assetResp = await client.PostAsync(uploadUrl, assetContent).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[BuildUploader] HTTP error uploading asset: {ex.Message}");
                        return;
                    }

                    if (!assetResp.IsSuccessStatusCode)
                    {
                        string err = await assetResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        Debug.LogError($"[BuildUploader] Failed to upload asset: {(int)assetResp.StatusCode} {assetResp.ReasonPhrase} — {err}");
                        return;
                    }

                    string assetJson = await assetResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    downloadUrl = ExtractJsonString(assetJson, "browser_download_url");
                }

                string linkToShare = !string.IsNullOrEmpty(downloadUrl) ? downloadUrl : htmlUrl;
                Debug.Log($"[BuildUploader] Uploaded {FormatBytes(zipBytes)} to GitHub release: {linkToShare}");
            }
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append($"\\u{(int)c:X4}");
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private static string ExtractJsonString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            string needle = $"\"{key}\"";
            int i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return null;
            i = json.IndexOf(':', i + needle.Length);
            if (i < 0) return null;
            i++;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '"') return null;
            i++;
            var sb = new StringBuilder();
            while (i < json.Length)
            {
                char c = json[i];
                if (c == '"') return sb.ToString();
                if (c == '\\' && i + 1 < json.Length)
                {
                    char n = json[i + 1];
                    switch (n)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        default: sb.Append(n); break;
                    }
                    i += 2;
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }
            return null;
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return $"{size:0.##} {units[unit]}";
        }
    }

    public class GitHubBuildUploaderWindow : EditorWindow
    {
        private string _githubToken;
        private string _githubRepo;
        private bool _enabled;

        [MenuItem("Tools/Build Uploader")]
        public static void Open()
        {
            var window = GetWindow<GitHubBuildUploaderWindow>("Build Uploader");
            window.minSize = new Vector2(480, 200);
        }

        private void OnEnable()
        {
            GitHubBuildUploader.MigrateLegacyPrefs();
            _githubToken = EditorPrefs.GetString(GitHubBuildUploader.GitHubTokenKey, string.Empty);
            _githubRepo = EditorPrefs.GetString(GitHubBuildUploader.GitHubRepoKey, string.Empty);
            _enabled = EditorPrefs.GetBool(GitHubBuildUploader.EnabledKey, false);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Build Uploader (GitHub Releases)", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            EditorGUI.BeginChangeCheck();
            _enabled = EditorGUILayout.Toggle("Enabled", _enabled);
            _githubRepo = EditorGUILayout.TextField("GitHub Repo (owner/name)", _githubRepo);
            _githubToken = EditorGUILayout.PasswordField("GitHub Token", _githubToken);
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetBool(GitHubBuildUploader.EnabledKey, _enabled);
                EditorPrefs.SetString(GitHubBuildUploader.GitHubRepoKey, _githubRepo ?? string.Empty);
                EditorPrefs.SetString(GitHubBuildUploader.GitHubTokenKey, _githubToken ?? string.Empty);
            }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "On a successful build, the build directory is zipped and uploaded as a GitHub release asset.\n\n" +
                "The GitHub token needs 'repo' scope (classic) or Contents: Read and write (fine-grained).",
                MessageType.Info);

            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_githubToken) && string.IsNullOrWhiteSpace(_githubRepo)))
            {
                if (GUILayout.Button("Clear All Settings"))
                {
                    _githubToken = string.Empty;
                    _githubRepo = string.Empty;
                    EditorPrefs.SetString(GitHubBuildUploader.GitHubTokenKey, string.Empty);
                    EditorPrefs.SetString(GitHubBuildUploader.GitHubRepoKey, string.Empty);
                    GUI.FocusControl(null);
                }
            }
        }
    }
}
