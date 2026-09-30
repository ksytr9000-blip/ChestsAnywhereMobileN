# Chests Anywhere Mobile Nav

Android touch helper for Chests Anywhere.

## Build on GitHub from a phone
1. Create an empty GitHub repository.
2. Upload **the contents of this folder** to the repository root (don't upload the outer folder itself).
3. Commit the files to `main`.
4. Open the repository's **Actions** tab.
5. Open **Build mod** and tap **Run workflow**. (A push to `main` also builds automatically.)
6. Wait for the green check.
7. Open that workflow run and download the **ChestsAnywhereMobileNav** artifact.
8. Extract the downloaded artifact. It contains the installable mod ZIP produced by SMAPI's build config.
9. Extract that mod ZIP into:
   `/storage/emulated/0/Android/data/abc.smapi.gameloader/files/Mods/`
10. The final folder should contain `manifest.json` and `ChestsAnywhereMobileNav.dll`.

Requires Chests Anywhere.
