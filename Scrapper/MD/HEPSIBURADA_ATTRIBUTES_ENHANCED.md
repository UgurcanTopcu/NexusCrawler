# Hepsiburada Product Attributes - Enhanced Extraction

## Overview
Enhanced the `ExtractHepsiburadaAttributes` method in `HepsiburadaScraper.cs` to extract product attributes (Ürün Özellikleri) more comprehensively, similar to how Trendyol scraper handles attributes.

## Changes Made

### Enhanced Attribute Extraction Methods

The updated `ExtractHepsiburadaAttributes` method now uses **5 different extraction strategies** to ensure maximum attribute coverage:

#### Method 1: JavaScript Extraction (Selenium - Most Reliable)
- **Priority**: Highest
- **How it works**: 
  - Scrolls to trigger lazy loading
  - Forces display of hydration-on-demand sections
  - Uses JavaScript to extract attributes from:
    - Table rows (`<table><tr><td>`)
    - Definition lists (`<dt><dd>`)
    - Data attributes (`[data-attribute-name]`)
    - Product specification sections
- **Benefits**: Handles dynamically loaded content that may not be in initial HTML

#### Method 2: HTML Table Parsing
- **Priority**: Fallback if Method 1 finds no attributes
- **How it works**: 
  - Searches for `<table><tr><td>` elements
  - Extracts key from first cell, value from second cell
- **Pattern**: Common in product specification tables

#### Method 3: Definition List Parsing
- **Priority**: Fallback if tables not found
- **How it works**: 
  - Searches for `<dt>` (definition term) and `<dd>` (definition description) pairs
  - Maps `<dt>` content as key, `<dd>` content as value
- **Pattern**: Common in semantic HTML for key-value data

#### Method 4: JSON Script Parsing
- **Priority**: Fallback for structured data
- **How it works**: 
  - Searches `<script>` tags containing attribute data
  - Extracts JSON key-value pairs using regex
  - Filters out common non-attribute keys (id, url, type, etc.)
- **Pattern**: Useful when data is embedded in JavaScript objects

#### Method 5: List Item Parsing with Colon Separator
- **Priority**: Last resort
- **How it works**: 
  - Searches list items (`<li>`), spec divs, feature divs
  - Looks for "Key: Value" pattern separated by colon
  - Extracts key-value pairs
- **Pattern**: Common in bullet point specifications

## Key Improvements

### 1. **Multiple Extraction Strategies**
   - No single point of failure - if one method fails, others are attempted
   - Comprehensive coverage of different HTML structures

### 2. **Enhanced Logging**
   - Detailed console output showing which extraction method succeeded
   - Sample attributes displayed for verification
   - Warning when no attributes found
   - Count of attributes extracted by each method

### 3. **Smart Filtering**
   - Removes duplicate whitespace
   - Filters out invalid or system keys
   - Validates key and value length
   - Prevents duplicate entries

### 4. **Lazy Loading Support**
   - Scrolls page to trigger lazy-loaded content
   - Forces display of hydration-on-demand sections
   - Waits for dynamic content to load

### 5. **Similar to Trendyol Implementation**
   - Follows same patterns as `TrendyolScraper.cs`
   - JavaScript extraction for dynamic content
   - Multiple HTML parsing fallbacks
   - JSON data extraction from scripts

## Expected Output

### Console Logs
```
[Hepsiburada] Extracting attributes for: Demirdöküm Nitromix P 24 kw Yoðuþmalý Kombi
[Hepsiburada] JS extracted 15 attributes
[Hepsiburada] Total attributes extracted: 15
[Hepsiburada] Sample attributes: Marka, Enerji Sýnýfý, Kapasite, Garanti Süresi, Tip
```

### Excel Output
The extracted attributes will appear as columns in the Excel export:
- Column headers: Attribute keys (e.g., "Marka", "Enerji Sýnýfý", "Kapasite")
- Cell values: Attribute values for each product

## Example Attributes Extracted

For a Hepsiburada product (e.g., Combi/Boiler), the following attributes might be extracted:

```json
{
  "Marka": "Demirdöküm",
  "Tip": "Yoðuþmalý Kombi",
  "Kapasite": "24 kW",
  "Enerji Sýnýfý": "A",
  "Garanti Süresi": "2 Yýl",
  "Renk": "Beyaz",
  "Yakýt Türü": "Doðal Gaz",
  "Baca Tipi": "Hermetik",
  "Kullaným Alaný": "Ev",
  "Maksimum Su Basýncý": "8 bar",
  "Minimum Su Basýncý": "0.8 bar",
  "Boyutlar": "740 x 400 x 298 mm",
  "Aðýrlýk": "29 kg"
}
```

## Benefits

1. **Completeness**: Multiple extraction methods ensure no attributes are missed
2. **Reliability**: Fallback mechanisms handle different HTML structures
3. **Performance**: JavaScript extraction is fast for Selenium method
4. **Compatibility**: Works with both Selenium and Scrape.do methods
5. **Debugging**: Comprehensive logging helps troubleshoot extraction issues

## Testing Recommendations

1. **Test with different product categories**:
   - Electronics (phones, laptops)
   - Home appliances (washing machines, refrigerators)
   - Heating equipment (combis, water heaters)
   - Fashion items (clothing, shoes)

2. **Verify attribute extraction**:
   - Check console logs for extraction success
   - Verify Excel has attribute columns
   - Compare with actual Hepsiburada product pages

3. **Test both scraping methods**:
   - Selenium method (default)
   - Scrape.do method

## Known Limitations

- Some product pages may have attributes in non-standard formats
- Dynamically loaded content requires Selenium (Scrape.do may miss some attributes)
- Very long attribute values may be truncated in Excel export

## Compatibility

- ? Works with existing `ProductInfo` model
- ? Compatible with `ExcelExporter` 
- ? Maintains backward compatibility
- ? Follows same patterns as `TrendyolScraper`

## Related Files

- `Scrapper\Services\HepsiburadaScraper.cs` - Main implementation
- `Scrapper\Services\TrendyolScraper.cs` - Reference implementation
- `Scrapper\Models\ProductInfo.cs` - Data model
- `Scrapper\Services\ExcelExporter.cs` - Export functionality
