**Verse Preflight is an unofficial Star Citizen fan project, not affiliated with the Cloud Imperium group of companies.** Free and open source (MIT). Official site: https://robertsspaceindustries.com

### Download

- **VersePreflight-win-Setup.exe**: installs for your Windows user (no admin needed), adds Start menu and desktop shortcuts, and updates itself.
- **VersePreflight-win-Portable.zip**: unzip anywhere (for example a cloud-synced folder shared between PCs). Settings are stored next to the app, one file per PC.

Windows may show a SmartScreen warning for new releases until the app is code-signed.

### New in 0.1.0-alpha.4

- **Fixed:** OPEN FOLDER on the Characters tab now opens the characters folder (#2).
- **Safer with downloaded characters:** a damaged or malformed `.chf` file now shows a clear error
  instead of freezing or closing the app.
- **Safer settings:** a folder link in the config that points outside its game folder is refused before
  any file is touched.
- **StarStrings:** unusually large downloads are refused instead of being installed.

See the commits since the previous release for everything that changed.
