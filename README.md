# Unity Build Uploader

A Unity Editor package that automatically zips your player build output and uploads it as a GitHub release asset after every successful build.

## What it does

When Unity finishes a successful player build, the package:

1. Zips the entire build output directory.
2. Creates a new GitHub release in your repo tagged `build-<platform>-<timestamp>`, marked as a prerelease.
3. Uploads the zip as an asset on that release.
4. Logs the release URL in the Unity Console.
5. Deletes the temp zip.

No file-size limit beyond GitHub's (2 GB per asset).

## Install

### Via Unity Package Manager (git URL)

1. In Unity, open **Window → Package Manager**.
2. Click the **+** button in the top-left → **Add package from git URL…**
3. Paste the repo URL, e.g. `https://github.com/squidypal/Github-Unity-Build-Uploader.git`.

### Via local path

1. Clone or copy this package folder somewhere on disk.
2. In Unity, open **Window → Package Manager**.
3. **+ → Add package from disk…** → select the `package.json` in this folder.

### Via `manifest.json`

Add this to your project's `Packages/manifest.json` under `"dependencies"`:

```json
"com.squidypal.build-uploader": "https://github.com/<owner>/BuildUploader.git"
```

## Setup

1. **Create a GitHub Personal Access Token.**
   - Classic: <https://github.com/settings/tokens> → *Generate new token (classic)* → scope `repo` (or `public_repo` if the repo is public).
   - Fine-grained: <https://github.com/settings/personal-access-tokens> → select the repo → permission **Contents: Read and write**.
   - For org repos, fine-grained tokens only work if the org owner has enabled fine-grained PAT access for the org. Use a classic token if unsure.

2. **Open the config window in Unity:** `Tools → Build Uploader`.

3. **Fill in:**
   - **Enabled** — tick this.
   - **GitHub Repo (owner/name)** — e.g. `squidypal/Github-Unity-Build-Uploader`. Just the slug, not a URL.
   - **GitHub Token** — paste the PAT.

Settings are stored in Unity's per-user `EditorPrefs`, not in the project — every teammate configures their own.

## Use

Just build normally (`File → Build And Run`, `File → Build Profiles → Build`, or any custom `[MenuItem]` that calls `BuildPipeline.BuildPlayer`). When the build succeeds, the Console will show:

```
[BuildUploader] Uploaded 77.6 MB to GitHub release: https://github.com/<owner>/<repo>/releases/download/build-<platform>-<timestamp>/<ProductName>_<platform>_<timestamp>.zip
```

Unity will appear to pause at the end of the build while the upload happens — that's intentional so the editor doesn't exit mid-upload on CI. Expect the pause to last roughly as long as it takes to upload the zip.

## Per-build toggle from a custom menu

If you already have a `[MenuItem("Build/...")]` in your project that calls `BuildPipeline.BuildPlayer`, you can wrap it to force the uploader on or off for a single build without stomping on the user's saved preference:

```csharp
using UnityEditor;
using BuildUploader.Editor;

public static class MyBuildMenus
{
    private static void RunWithUploader(bool upload, System.Action build)
    {
        bool previous = EditorPrefs.GetBool(GitHubBuildUploader.EnabledKey, false);
        EditorPrefs.SetBool(GitHubBuildUploader.EnabledKey, upload);
        try { build(); }
        finally { EditorPrefs.SetBool(GitHubBuildUploader.EnabledKey, previous); }
    }

    [MenuItem("Build/With GitHub Release/Windows Release")]
    public static void UploadWindowsRelease() => RunWithUploader(true, MyBuilder.BuildWindowsRelease);

    [MenuItem("Build/Without GitHub Release/Windows Release")]
    public static void NoUploadWindowsRelease() => RunWithUploader(false, MyBuilder.BuildWindowsRelease);
}
```

## Troubleshooting

- **Nothing happens / no `[BuildUploader]` log lines.** Compile errors somewhere. Check the Console with the error filter on. Also make sure Unity has finished compiling before you hit Build — if you see "You are building a player, but you have uncompiled code changes" in the log, rebuild after the recompile finishes.
- **`Failed to create GitHub release: 404 Not Found`.** Wrong repo slug, or your token can't see that repo. Run `curl -H "Authorization: Bearer YOUR_TOKEN" https://api.github.com/repos/OWNER/NAME` to sanity-check both.
- **`Failed to create GitHub release: 401 Unauthorized`.** Token is being rejected. Usually whitespace/newline pasted into the token field — clear it and paste again. The script trims on read as a safety net but make sure what you saved is clean.
- **Fine-grained token 404s even for public repos.** The org hasn't enabled fine-grained PATs. Switch to a classic token with `repo` scope.
- **Build appears stuck at the very end.** The upload is in progress on a background thread. For a large zip (hundreds of MB on a slow connection) this can take minutes. Check the GitHub releases page — if the release has appeared with the asset, it's nearly done.

## Clean up old automated releases

Every build creates a new release. To purge old ones, delete them on GitHub under `https://github.com/<owner>/<repo>/releases`, or script it via the GitHub CLI:

```bash
gh release list --limit 100 | awk '/^build-/ {print $1}' | xargs -I {} gh release delete {} --yes --cleanup-tag
```

## Package layout

```
com.squidypal.build-uploader/
├── package.json
├── README.md
├── CHANGELOG.md
└── Editor/
    ├── BuildUploader.Editor.asmdef
    └── GitHubBuildUploader.cs
```

## Config keys

Stored in `EditorPrefs`:

| Key | Type | Purpose |
|---|---|---|
| `GitHubBuildUploader.Enabled` | bool | Master on/off |
| `GitHubBuildUploader.GitHubToken` | string | Personal access token |
| `GitHubBuildUploader.GitHubRepo` | string | `owner/name` slug |
