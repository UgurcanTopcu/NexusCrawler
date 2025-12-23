# FTP Connection Troubleshooting Guide

## Current Error
```
System.Net.WebException: The underlying connection was closed: An unexpected error occurred on a receive
```

This error occurs at `request.GetRequestStream()`, meaning the FTP server is rejecting the connection during data channel setup.

## Possible Causes

### 1. **Firewall Blocking Data Channel**
- Passive mode requires the server to open a random high port
- Active mode requires YOUR machine to accept incoming connections
- **Solution**: Try both modes, one might work

### 2. **Directory Doesn't Exist**
- FTP servers might reject uploads to non-existent paths
- **Solution**: Create `/images/products/` directory first

### 3. **Incorrect Path Format**
- Some FTP servers are picky about path separators
- **Solution**: Try different path formats

## Testing Steps

### Step 1: Run Connection Tester

Add this to your `Program.cs` temporarily (before `app.Run()`):

```csharp
// TEST FTP CONNECTION
Console.WriteLine("Running FTP connection test...");
Scrapper.Services.FtpConnectionTester.TestConnection();
Console.WriteLine("Press any key to continue...");
Console.ReadKey();
```

This will:
- ? Test basic FTP connection
- ? Check if directories exist
- ? Try uploading with PASSIVE mode
- ? Try uploading with ACTIVE mode

### Step 2: Analyze Results

Look for which test succeeds:
- If Test 4 (PASSIVE) works ? Use `UsePassive = true`
- If Test 5 (ACTIVE) works ? Use `UsePassive = false`
- If both fail ? Directory/permission issue

### Step 3: Fix Based on Results

**If PASSIVE works:**
```csharp
request.UsePassive = true;
```

**If ACTIVE works:**
```csharp
request.UsePassive = false;
```

**If directory doesn't exist:**
You need to manually create `/images/products/` on the FTP server first, or use a different path like `/` (root).

## Alternative: Try Root Directory

If `/images/products/` doesn't work, try uploading to root:

```csharp
var ftpPath = $"/{fileName}"; // Upload to root
var ftpUrl = $"ftp://4d2a1dbf530c769e.mncdn.com{ftpPath}";
```

## Quick Test Command

You can also test from Windows Command Prompt:

```cmd
ftp 4d2a1dbf530c769e.mncdn.com
# Username: mmstr_4579ba15
# Password: Ut123.#@

# Then type:
dir
mkdir images
cd images
mkdir products
cd products
```

This will show you:
1. If you can connect
2. What directories exist
3. If you can create directories

## Common Solutions

### Solution 1: Use Root Directory
```csharp
var ftpPath = $"/{fileName}";
```

### Solution 2: Try PORT mode (Active)
```csharp
request.UsePassive = false;
```

### Solution 3: Check Filestash Settings
Since you mentioned Filestash works, check what settings it uses:
- Look in Filestash configuration
- It might be using a different port (990 for FTPS?)
- It might be using a different authentication method

### Solution 4: Try Port 990 (FTPS)
```csharp
var ftpUrl = "ftps://4d2a1dbf530c769e.mncdn.com:990/...";
// May need to enable SSL
request.EnableSsl = true;
```
