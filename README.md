<div align="center">

# 💬 BtChat

**Serverless, internet-free peer-to-peer (P2P) messaging, file transfer, and proxy sharing for Android & Windows.**

![.NET MAUI](https://img.shields.io/badge/.NET-MAUI%2010-512BD4?logo=dotnet&logoColor=white)
![Android](https://img.shields.io/badge/Android-6.0%2B-3DDC84?logo=android&logoColor=white)
![Windows](https://img.shields.io/badge/Windows-10%2B-0078D4?logo=windows&logoColor=white)
![License](https://img.shields.io/badge/License-MIT-blue)

[Key Features](#-key-features) • [How It Works](#-how-it-works) • [Internet Sharing](#-internet-sharing-proxy) • [Build Instructions](#%EF%B8%8F-build-instructions) • [Contributing](#-contributing)

</div>

---

## Overview

**BtChat** is a cross-platform peer-to-peer messenger designed for Android and Windows. Two devices discover and communicate with each other directly via **Bluetooth**, **Wi-Fi / Hotspot**, or an **IP Address**. No server, cloud infrastructure, or internet connection is required—your data stays strictly between your devices.

---

## ⚡ Key Features

| Feature | Description |
| :--- | :--- |
| 💬 **Text Messaging** | Send, edit, and delete messages, rename chats, and clear history instantly. |
| 📎 **File Transfer** | Send photos, videos, or documents with real-time progress indicators and cancellation support. |
| 🎙️ **Voice Notes** | Hold-to-record voice messaging with variable playback speeds ($1x, 1.5x, 2x$). |
| 📞 **Voice & Video Calls** | High-quality direct calling with camera toggle, speaker routing, and bandwidth management. |
| 🎬 **Built-in Media Player** | Integrated player supporting embedded or external subtitles (SRT/VTT) with custom timing and sizing. |
| 📷 **Instant QR Pairing** | Connect devices instantly by displaying a QR code on one screen and scanning it on another. |
| 🔎 **Device Discovery** | Scan local networks to automatically find nearby devices running BtChat. |
| 🌐 **Internet Sharing (Proxy)** | Proxy one device's internet connection to another directly or via reverse tunnel connections. |
| 🔔 **Background Service** | Keeps connections alive through persistent system notifications even when minimized. |
| 🌗 **Customization** | Light, Dark, and System theme modes with full Right-to-Left (RTL) localization support. |

---

## 🚀 How It Works

```
+-----------------------------------------------------------------+
|                         BtChat Network                          |
|                                                                 |
|  [Device A] <==== Bluetooth / Wi-Fi / Hotspot ====> [Device B]  |
|                                                                 |
|                 * No Cloud  * No Account Needed                 |
+-----------------------------------------------------------------+
```

1. **Launch** BtChat on both devices.
2. **Select Connection Type**:
   - **Bluetooth:** Pair devices once in system settings, then connect in-app.
   - **Wi-Fi / Local Network:** Connect both devices to the same router or enable a personal hotspot.
   - **QR Code:** Display a connection QR code on one device and scan it with the second device.
3. **Start Chatting:** Send text, files, or initiate audio/video calls with zero middleman latency.

---

## 🌐 Internet Sharing (Proxy)

BtChat allows one device to share its internet connectivity with another using **HTTP/HTTPS CONNECT** and **SOCKS5** protocols (*UDP is not currently supported*).

- **Direct Mode:** The host device opens a listening port (default `8080`), and the client connects directly via IP or QR code scan.
- **Reverse Connection Mode:** Ideal for situations where incoming connections are blocked on the host (e.g., when running a tunnel or VPN on Android). The sharing device dials *out* to the receiver instead. The receiver runs a local proxy at `127.0.0.1`, routing every request seamlessly through the pre-established reverse link.
- **Pairing & Security:** Optional password protection utilizing HMAC-SHA256 challenge-response authentication—passwords are never sent over the wire.
- **Usage & Controls:** Set as the system proxy on Windows with one click or set manual/app-level proxies on Android. Features user access controls, local-network-only modes, transfer quotas, and real-time bandwidth metrics.

> **Disclaimer:** Use this feature responsibly and only on networks and devices you own or have explicit authorization to use.

---

## 🛠️ Build Instructions

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download) with the **MAUI** workload installed.

### Build Commands

```bash
# Install the .NET MAUI workload
dotnet workload install maui

# Build for Android
dotnet build -f net10.0-android -c Release

# Build for Windows (from a Windows machine)
dotnet build -f net10.0-windows10.0.19041.0 -c Release
```

### Minimum System Requirements
- **Android:** 6.0 (API Level 23) or higher
- **Windows:** Windows 10 (Build 17763) or higher

---

## 📁 Project Structure

```
BtChat/
 ├── Platforms/
 │    ├── Android/    # Native Android Bluetooth, Audio, Camera, QR, and Background Services
 │    └── Windows/    # Native Windows Bluetooth, Audio endpoints, and File System integrations
 └── BtChat/          # Shared App Logic: UI Views, P2P Protocols, Proxy Core, & Localizations
```

---

