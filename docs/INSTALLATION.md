# Installation Guide

Zeus is distributed as native installers for Windows, macOS, and Linux.

## Windows

### Requirements
- Windows 10 or later (64-bit)
- 4 GB RAM minimum, 8 GB recommended

### Installation Steps

1. Download `Zeus-X.Y.Z-win-x64-setup.exe` from the [latest release](https://github.com/Kb2uka/openhpsdr-zeus/releases/latest)
2. Run the installer
3. Follow the installation wizard prompts
4. Zeus will be installed to `C:\Program Files\Zeus` by default
5. A desktop shortcut and Start Menu entry will be created
6. Launch Zeus from the Start Menu or desktop shortcut
7. Your default browser will open to `http://localhost:6060`

### Notes
- The installer includes the .NET runtime and all dependencies
- Windows Defender SmartScreen may show a warning for unsigned applications - click "More info" then "Run anyway"
- To uninstall, use Windows Settings > Apps > Zeus

---

## macOS

### Requirements
- macOS 11 (Big Sur) or later
- Apple Silicon (M1/M2/M3) or Intel processor
- 4 GB RAM minimum, 8 GB recommended

### Installation Steps

1. Download the appropriate DMG for your Mac:
   - **Apple Silicon (M1/M2/M3)**: `Zeus-X.Y.Z-macos-arm64.dmg`
   - **Intel**: `Zeus-X.Y.Z-macos-x64.dmg`

2. Open the downloaded DMG file

3. Drag `Zeus.app` to your Applications folder

4. **IMPORTANT**: Remove the quarantine attribute
   ```bash
   xattr -cr /Applications/Zeus.app
   ```
   This step is **required** because Zeus is not signed by a registered Apple Developer.

5. Launch Zeus from your Applications folder or Launchpad

6. Your default browser will open to `http://localhost:6060`

### Troubleshooting

If you see **"Zeus.app is damaged and can't be opened"**:
- You forgot to run the `xattr -cr` command. Open Terminal and run:
  ```bash
  xattr -cr /Applications/Zeus.app
  ```

If you see **"Zeus.app can't be opened because it is from an unidentified developer"**:
- Right-click on Zeus.app and select "Open"
- Click "Open" in the security dialog
- Or run `xattr -cr /Applications/Zeus.app` as above

### Uninstallation
- Drag `Zeus.app` from Applications to Trash

---

## Linux

### Requirements
- Linux x64 distribution (Ubuntu 20.04+, Debian 11+, Fedora 35+, or equivalent)
- libfftw3 (usually pre-installed on most distributions)
- 4 GB RAM minimum, 8 GB recommended
- Desktop environment (for automatic browser launching)

### Installation Steps

1. Download `zeus-X.Y.Z-linux-x64.tar.gz` from the [latest release](https://github.com/Kb2uka/openhpsdr-zeus/releases/latest)

2. Extract the archive:
   ```bash
   tar -xzf zeus-X.Y.Z-linux-x64.tar.gz
   ```

3. (Optional) Move to a permanent location:
   ```bash
   sudo mv zeus-X.Y.Z-linux-x64 /opt/zeus
   ```

4. Run Zeus:
   ```bash
   cd /opt/zeus  # or wherever you extracted it
   ./zeus
   ```

5. Your default browser will open to `http://localhost:6060`

### Installing libfftw3 (if needed)

**Ubuntu/Debian:**
```bash
sudo apt-get install libfftw3-3
```

**Fedora:**
```bash
sudo dnf install fftw3
```

**Arch:**
```bash
sudo pacman -S fftw
```

### Creating a Desktop Launcher (Optional)

Create `~/.local/share/applications/zeus.desktop`:
```ini
[Desktop Entry]
Type=Application
Name=Zeus
Comment=OpenHPSDR SDR Client
Exec=/opt/zeus/zeus
Icon=/opt/zeus/icon.png
Terminal=false
Categories=Network;HamRadio;
```

Then run:
```bash
update-desktop-database ~/.local/share/applications
```

### Uninstallation
- Simply delete the extracted directory

---

## First Run

On the **first run only**, Zeus will initialize WDSP/FFTW wisdom files. This takes 1-3 minutes on a modern CPU. You'll see:

```
Optimizing FFT sizes through 262145
Please do not close this window until wisdom plans are completed.
```

**Do not** click "Discover" or "Connect" in the web UI until you see:

```
wdsp.wisdom ready result=1 (built)
```

Subsequent starts will be instant as the wisdom is cached.

---

## Updating Zeus

When a new version is available, Zeus will show a notification in the Settings > About panel.

### Windows
- Download and run the new installer
- It will automatically upgrade your existing installation

### macOS
1. Download the new DMG
2. Drag the new `Zeus.app` to Applications (replace the old one)
3. Run `xattr -cr /Applications/Zeus.app` again

### Linux
1. Download the new tarball
2. Extract to replace your existing installation
3. Your settings are preserved (stored in `~/.local/share/Zeus/`)

---

## Configuration and Data Locations

### Windows
- Settings: `%LOCALAPPDATA%\Zeus\zeus-prefs.db`
- WDSP Wisdom: `%LOCALAPPDATA%\Zeus\wdspWisdom00`
- Logs: `%LOCALAPPDATA%\Zeus\logs\`

### macOS
- Settings: `~/Library/Application Support/Zeus/zeus-prefs.db`
- WDSP Wisdom: `~/Library/Application Support/Zeus/wdspWisdom00`
- Logs: `~/Library/Application Support/Zeus/logs/`

### Linux
- Settings: `~/.local/share/Zeus/zeus-prefs.db`
- WDSP Wisdom: `~/.local/share/Zeus/wdspWisdom00`
- Logs: `~/.local/share/Zeus/logs/`

---

## Support

- **Issues**: https://github.com/Kb2uka/openhpsdr-zeus/issues
- **Documentation**: https://github.com/Kb2uka/openhpsdr-zeus
- **License**: GNU GPL v2 or later

---

## Building from Source

If you prefer to build Zeus from source instead of using the pre-built installers, see the [README.md](../README.md) for developer setup instructions.
