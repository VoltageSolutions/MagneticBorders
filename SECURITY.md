# Security Policy

## Supported versions

Only the latest release is supported. Please update to it before reporting a problem.

## What the app exposes

Magnetic Borders runs on your own Windows session. It does not use the network. It watches window events, moves and resizes other windows on your desktop, and listens on a named pipe (`\\.\pipe\MagneticBorders`) that only your user account can open. Settings live in `%LOCALAPPDATA%\MagneticBorders\`. It never runs elevated unless you start it that way.

## Reporting a vulnerability

Please open an issue on the Issues page.

If you think the details should not be public, open an issue that says so without including the details, and I will follow up with you.
