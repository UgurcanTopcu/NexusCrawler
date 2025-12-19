# Required NuGet Packages for Image Processing

## Packages to Install

Run these commands in the Package Manager Console or add them to your .csproj file:

```bash
# For FTP functionality
dotnet add package FluentFTP --version 51.0.0

# For image processing (resize, format conversion)
dotnet add package SixLabors.ImageSharp --version 3.1.5
```

## Package Details

### 1. **FluentFTP** (v51.0.0)
- **Purpose**: FTP client library for uploading images to CDN
- **Features**: Async operations, progress reporting, directory management
- **License**: MIT

### 2. **SixLabors.ImageSharp** (v3.1.5)
- **Purpose**: Image processing library for resizing images to 1000x1000
- **Features**: Resize, crop, format conversion, quality optimization
- **License**: Apache 2.0 (free for commercial use)

## Alternative: Add to .csproj

Add these lines inside the `<ItemGroup>` section of your `Scrapper.csproj`:

```xml
<PackageReference Include="FluentFTP" Version="51.0.0" />
<PackageReference Include="SixLabors.ImageSharp" Version="3.1.5" />
```

## Verification

After installing, verify the packages are installed by checking:
- `Scrapper.csproj` file contains the PackageReferences
- Packages appear in Solution Explorer under Dependencies > Packages
- Build completes without errors

## Notes

- **FluentFTP**: Handles all FTP operations including authentication, directory creation, and file uploads with progress tracking
- **ImageSharp**: Modern, high-performance image processing library that supports various formats and operations
- Both packages are actively maintained and production-ready
