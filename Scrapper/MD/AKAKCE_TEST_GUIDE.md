# Akakce Scraper - Test Excel Template

## How to Create Test Excel File

1. Open Excel
2. In Column A, Row 1, type: **Product URLs**
3. Starting from Row 2, paste these Akakce URLs:

```
https://www.akakce.com/cep-telefonu/en-ucuz-iphone-15-fiyati,1745758198.html
https://www.akakce.com/cep-telefonu/en-ucuz-samsung-galaxy-s24-ultra-512-gb-fiyati,1697516298.html
https://www.akakce.com/laptop/en-ucuz-apple-macbook-pro-14-m3-pro-18gb-512gb-fiyati,1703598098.html
```

4. Save as `akakce_test.xlsx`

## Expected Results

After scraping, you should get an Excel file with **3 sheets**:

### Sheet 1: Products Summary
- iPhone 15 128 GB with ~224 sellers
- Samsung Galaxy S24 Ultra with sellers
- MacBook Pro 14 with sellers

### Sheet 2: All Sellers
- Flat list of ALL sellers from all products
- Columns: Product ID, Product Name, Rank, Seller Name, Price, etc.

### Sheet 3: Detailed View
- Combined view with product info + seller info per row
- Best for filtering and analysis

## Troubleshooting

If Excel is empty, check the **Console Output** for:

```
[AkakceService] ========== SCRAPING SESSION START ==========
[AkakceService] Total URLs found: X
[Akakce] Product ID: XXXXXXX
[Akakce] Name: Product Name Here
[Akakce] Seller X: SellerName - Price
[AkakceService] Total products collected: X
[Akakce Export] ========== EXPORT START ==========
[Akakce Export] Products to export: X
[Akakce Export] SUCCESS: File saved with 3 sheets
```

### Common Issues:

1. **No sellers extracted**: Page structure changed, selectors need update
2. **Excel has headers only**: Products list is empty or sellers array is empty
3. **License error**: EPPlus license context not set properly (now fixed)

## Debug Mode

To see detailed logs, run the app and watch the Console output. Each step shows:
- URLs being scraped
- Sellers found per product  
- Products added to collection
- Excel export progress
