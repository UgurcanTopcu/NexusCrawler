# ??? Cloudflare Bypass Guide for Akakce Scraper

## The Problem

Akakce uses **Cloudflare Turnstile** protection, which shows "Verify you are human" after detecting:
- Too many requests from same IP
- Bot-like behavior (no mouse movements, fast requests)
- WebDriver detection

## ? Solution Implemented

### 1. **Automatic Turnstile Checkbox Clicking**

The scraper attempts to click the Turnstile checkbox automatically:

```csharp
private async Task<bool> TryClickTurnstileCheckbox()
{
    // Try multiple methods to find and click the checkbox
    // 1. Look for Turnstile iframe
    // 2. Find checkbox with cf- in class/id
    // 3. Click challenge container
}
```

### 2. **Long Delays Between Products (5-10 seconds)**

The scraper waits 5-10 seconds between each product page to avoid triggering Cloudflare:

```csharp
// In AkakceScraper.cs
private const int MIN_DELAY_BETWEEN_PRODUCTS = 5;
private const int MAX_DELAY_BETWEEN_PRODUCTS = 10;

// Plus additional delays in AkakceScraperService.cs
private const int MIN_DELAY_BETWEEN_PRODUCTS_MS = 3000;
private const int MAX_DELAY_BETWEEN_PRODUCTS_MS = 6000;
```

**Total delay per product: ~8-16 seconds**

### 3. **30-Second Cooldown on Cloudflare Detection**

When Cloudflare blocks a request, the scraper waits 30 seconds:

```csharp
if (product.ErrorMessage?.Contains("Cloudflare") == true)
{
    await Task.Delay(30000); // 30 second cooldown
}
```

### 4. **Human-Like Behavior Simulation**

Before each page load:
- Random mouse movements
- Random scrolling
- Variable delays

### 5. **Persistent Browser Profile**

Cookies and session data are saved at:

```
%TEMP%\AkakceChromeProfile
```

After solving CAPTCHA once, it's saved for future sessions.

## ?? How It Works Now

### First Product (or After Cooldown):
1. Navigate to product page
2. Wait 3-5 seconds for Cloudflare check
3. If Turnstile appears ? Try to click automatically
4. If click fails ? Wait for manual solve (up to 90 seconds)
5. Extract seller data

### Subsequent Products:
1. Wait 5-10 seconds (scraper) + 3-6 seconds (service) = 8-16 seconds
2. Simulate human behavior (mouse, scroll)
3. Navigate to next product
4. If Cloudflare triggers ? 30 second cooldown

## ?? Expected Scraping Times

| Products | Estimated Time |
|----------|----------------|
| 5 | ~2-3 minutes |
| 10 | ~4-6 minutes |
| 20 | ~8-12 minutes |
| 50 | ~20-30 minutes |
| 100 | ~40-60 minutes |

**This is slower but much more reliable!**

## ?? When Manual Intervention is Needed

The browser will show:

```
[Akakce] ?? Turnstile challenge detected - attempting to solve...
[Akakce] ? Please click the 'Verify you are human' checkbox manually...
```

**Action**: Click the checkbox in the visible Chrome window.

After clicking:
- The scraper will continue automatically
- Cookies are saved for future requests
- May not need to click again for a while

## ?? Tips for Success

### 1. **Keep the Browser Window Visible**
Cloudflare checks if the window is focused/visible.

### 2. **Don't Minimize Chrome**
Minimized windows may trigger additional checks.

### 3. **Start with Small Batches**
- First run: 5 products
- If successful: 10-20 products
- Work up slowly

### 4. **Run During Off-Peak Hours**
- Late night (Turkey time) = less strict
- Early morning = less strict
- Peak hours = more CAPTCHAs

### 5. **Use Residential IP**
If using VPN or datacenter IP, Cloudflare is more aggressive.

## ?? If Still Having Issues

### Option 1: Clear Profile and Re-verify
```powershell
# Delete the profile folder
Remove-Item -Path "$env:TEMP\AkakceChromeProfile" -Recurse -Force
```

### Option 2: Increase Delays
In `AkakceScraper.cs`:
```csharp
private const int MIN_DELAY_BETWEEN_PRODUCTS = 10; // Increase to 10s
private const int MAX_DELAY_BETWEEN_PRODUCTS = 20; // Increase to 20s
```

### Option 3: Use a Different IP
- Restart router (if dynamic IP)
- Use a VPN with residential IPs
- Try a different network

### Option 4: Reduce Batch Size
Only scrape 3-5 products at a time, then wait a few minutes.

## ?? Progress Messages

| Message | Meaning |
|---------|---------|
| `?? Waiting Xs to avoid Cloudflare...` | Normal delay between products |
| `?? Turnstile challenge detected` | CAPTCHA appeared |
| `? Please click the checkbox manually...` | Manual action needed |
| `? Cloudflare challenge passed!` | Successfully bypassed |
| `? Cloudflare detected - adding 30s cooldown...` | Got blocked, waiting |

## ?? Legal & Ethical Notes

1. **Personal Use Only** - Don't resell scraped data
2. **Respect Rate Limits** - The delays are there for a reason
3. **Don't Overload** - Scraping thousands of pages hurts the site
4. **Check Terms of Service** - Akakce may prohibit scraping

---

## Summary

The key to reliable Akakce scraping:

1. ? **Long delays** (8-16 seconds per product)
2. ? **Human simulation** (mouse, scroll)
3. ? **Persistent cookies** (CAPTCHA solve persists)
4. ? **Automatic Turnstile clicking** (when possible)
5. ? **Manual fallback** (90 second timeout)
6. ? **30s cooldown** on blocks

This makes scraping slow but **much more reliable** than fast scraping that gets blocked constantly.
