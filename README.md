# Riot Account Switcher

A fast, lightweight, and safe account switcher for Riot Games (Valorant / League of Legends) built with **C# / .NET / WPF** and UI automation via **FlaUI**.

---

## 🌟 Key Features

* **Safe Automation via FlaUI:** Automates credential entry and clicks through official Windows UI Automation patterns. It does not tamper with game memory, inject DLLs, or hook processes, making it fully safe from Vanguard bans.
* **Cursor Position Preservation:** Automatically caches and restores the user's cursor position (`FlaUI.Core.Input.Mouse.Position`) during programmatic clicks, eliminating cursor jumps.
* **Stale-While-Revalidate (SWR) Caching:** Loads existing rank information immediately from local cache (`rank_cache.json`) on launch, then asynchronously refreshes expired ranks in the background.
* **Local Credential Protection:** Passwords stored in `accounts.json` are obfuscated using a custom cryptographic pipeline (XOR cipher + reverse string + Base64 encoding) rather than stored as plaintext.
* **Automatic Riot ID Resolution:** Reads active session credentials and ports directly from the Riot Client `lockfile` to resolve and update user tags (`game_name#tag`) automatically.
* **Dynamic Rank Display:** Queries current competitive tier information and renders color-coded badges matching in-game ranks (Iron through Radiant).
* **Companion App Integration:** Optional auto-launch trigger for external trackers (e.g., Overwolf Valorant Tracker) upon successful login.
* **Custom Dark Theme UI:** Features a streamlined, 5–6px transparent `ScrollBar` template with navigation buttons removed for an unobtrusive aesthetic.

---

## 🛠️ Tech Stack & Architecture

* **Framework & UI:** C#, .NET, WPF (Windows Presentation Foundation)
* **IDE:** Microsoft Visual Studio 2022
* **Core Libraries:**
  * `FlaUI.Core` & `FlaUI.UIA3`: Modern UI Automation engine for controlling the Riot Client window.
  * `System.Text.Json`: High-performance JSON serialization for config, accounts, and rank cache.
* **Packaging & Distribution:** WinRAR SFX installer with custom HTML UI and automated Registry cleanup scripts (`HKLM`).

---

## ⚙️ System Requirements

* Windows 10 / Windows 11 (64-bit)
* Riot Client (Valorant / League of Legends) installed
* Execution privileges: **Run as Administrator** (enforced via `app.manifest`'s `requireAdministrator` level to enable UI automation over elevated Riot processes).

---

## 🚀 Getting Started

### 1. Installation (Pre-built Binaries)

1. Download the latest installer (`RiotSwitcher_Setup_vX.X.X.exe`) from the [Releases](https://github.com/kizcntt/RiotSwitcher/releases) page.
2. Run the installer as Administrator.
3. On first launch, if Riot Client is not currently running, select your local path to `RiotClientServices.exe`.
4. Enter your credentials, click **Save**, and select any account to switch seamlessly.

### 2. Building from Source

```bash
# Clone the repository
git clone https://github.com/kizcntt/RiotSwitcher.git

# Navigate to project root
cd RiotSwitcher

# Build in Release configuration
dotnet build -c Release
```

---

## 📁 Local Configuration Files

* `setting.ini`: Stores the verified local file path to `RiotClientServices.exe`.
* `accounts.json`: Encrypted local repository of saved credentials.
* `rank_cache.json`: Cache file storing player ranks and cache expiration timestamps (TTL).

---

## ⚠️ Disclaimer

This is an unofficial, open-source personal utility and is **not affiliated with, endorsed by, or sponsored by Riot Games**. 

The tool interacts solely with official client interfaces via standard Windows accessibility and automation APIs. The developers assume no liability for misuse.

---

## 📄 License

This project is open-source and distributed under the [MIT License](LICENSE).