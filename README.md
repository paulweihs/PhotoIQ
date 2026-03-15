# PhotoIQ Pro

AI-powered photo management for Windows.

## Requirements

- Windows 10/11
- Visual Studio 2022 with ".NET desktop development" workload

## Quick Start

1. Extract ZIP to `C:\Dev\PhotoIQPro`
2. Open `PhotoIQPro.sln` in Visual Studio 2022
3. Press F5 to build and run

## Project Structure

- **PhotoIQPro.Core** - Domain models & interfaces
- **PhotoIQPro.Data** - EF Core + SQLite
- **PhotoIQPro.Services** - Import & thumbnails
- **PhotoIQPro.AI** - CLIP integration
- **PhotoIQPro.Desktop** - WPF application
- **PhotoIQPro.Common** - Shared settings
- **PhotoIQPro.Tests** - Unit tests
