# Lockstep roadmap

Everything else on the roadmap is done. One item is still pending.

## Pending

### Windows 11 ISO builder

Build Windows 11 install media that is already cleaned up before Windows is installed, in the spirit of WinUtil's MicroWin:

- **Start from an official image:** take a Windows 11 ISO downloaded from Microsoft, and pick the edition to keep (Home, Pro…).
- **Remove bloatware offline:** take out preinstalled Store apps, Edge, OneDrive, Copilot and other optional components from the image itself.
- **Skip the online-account push:** an unattended answer file (`autounattend.xml`) that allows a local account and skips the privacy and "Let's connect your phone" screens in setup.
- **Apply a Lockstep profile on first sign-in:** the same `.json` profile used by `--config`, so a fresh install comes up already private and configured.
- **Optional drivers:** add drivers from a folder (for example network or storage drivers the installer needs).
- **Output:** a new bootable ISO, or write it straight to a USB stick.

This is the largest remaining feature. It needs DISM image servicing, `oscdimg` to build the ISO, and testing in a virtual machine before release.
