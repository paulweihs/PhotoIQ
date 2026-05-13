# PhotoIQ Pro — Branding Assets

## Files

| File | Size | Use |
|------|------|-----|
| `icon.svg` | 256×256 | Source for app icon (.ico) |
| `logo-horizontal.svg` | 480×80 | Sidebar, about page, website header |
| `splash.svg` | 480×300 | Splash / about screen |
| `installer-banner.svg` | 497×58 | Inno Setup / NSIS installer header |

## Converting to ICO (Windows App Icon)

Install Inkscape, then run:

```powershell
# Export PNG at each required size
$sizes = 16, 24, 32, 48, 64, 128, 256
foreach ($s in $sizes) {
    inkscape icon.svg --export-png="icon-$s.png" --export-width=$s --export-height=$s
}

# Combine into .ico  (requires ImageMagick)
magick icon-16.png icon-24.png icon-32.png icon-48.png icon-64.png icon-128.png icon-256.png icon.ico
```

Or use any online SVG→ICO converter (e.g. https://convertio.co/svg-ico/).

The resulting `icon.ico` goes in:
- `src/PhotoWell.Desktop/` — reference in the `.csproj` as `<ApplicationIcon>`
- The installer script (Inno Setup: `SetupIconFile`)

## Referencing the Icon in the .csproj

```xml
<PropertyGroup>
  <ApplicationIcon>icon.ico</ApplicationIcon>
</PropertyGroup>
```

## Installer Assets (Inno Setup)

```ini
[Setup]
WizardImageFile=installer-banner.svg   ; convert to 497x314 BMP first
WizardSmallImageFile=icon.svg          ; convert to 55x55 BMP first
SetupIconFile=icon.ico
```

Convert SVGs to BMP with ImageMagick:
```powershell
magick installer-banner.svg -resize 497x314 installer-banner.bmp
magick icon.svg -resize 55x55 icon-small.bmp
```

## Colors

| Token | Hex | Use |
|-------|-----|-----|
| Background dark | `#060a14` | Deep navy — window/icon bg |
| Background mid  | `#0d1830` | Mid navy — panels |
| Accent blue     | `#0078d4` | Primary accent, aperture blades |
| Accent light    | `#60aaff` | Glow, sparkle, highlights |
| Text primary    | `#ffffff` | Headings |
| Text secondary  | `#3a6090` | Subtext, taglines |
