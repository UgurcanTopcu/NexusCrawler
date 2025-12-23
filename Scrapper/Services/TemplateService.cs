using Scrapper.Models;

namespace Scrapper.Services;

public class TemplateService
{
    private readonly Dictionary<string, ExportTemplate> _templates = new();

    public TemplateService()
    {
        InitializeTemplates();
    }

    private void InitializeTemplates()
    {
        // Trendyol Kettle Template (MediaMarkt)
        var kettleTemplate = new ExportTemplate
        {
            Name = "trendyol_kettle",
            Description = "MediaMarkt Kettle Template for Trendyol Products",
            Columns = new List<TemplateColumn>
            {
                new() { DisplayName = "Kategori", TechnicalName = "CATEGORY", MappingHint = "CATEGORY", 
                    DefaultValue = "KETTLE", Mapping = new() { Field = ProductField.StaticValue } },
                
                new() { DisplayName = "SHOP_SKU", TechnicalName = "SHOP_SKU", MappingHint = "BARCODE",
                    Mapping = new() { Field = ProductField.Barcode } },
                
                new() { DisplayName = "Baþlýk", TechnicalName = "TITLE__TR_TR", MappingHint = "Product Name",
                    Mapping = new() { Field = ProductField.Name } },
                
                new() { DisplayName = "EAN", TechnicalName = "EAN", MappingHint = "Barcode",
                    Mapping = new() { Field = ProductField.Barcode } },
                
                new() { DisplayName = "Marka", TechnicalName = "BRAND", MappingHint = "BRAND",
                    Mapping = new() { Field = ProductField.Brand } },
                
                new() { DisplayName = "Manufacturer Part Number (MPN)", TechnicalName = "ATTR_PROD_MP_Manufacturer_PartNumber", 
                    MappingHint = "", DefaultValue = "" },
                
                new() { DisplayName = "Ürün Açýklamasý", TechnicalName = "Product_Description__TR_TR", MappingHint = "Description",
                    Mapping = new() { Field = ProductField.Description } },
                
                new() { DisplayName = "Age Restriction (in years) (TR)", TechnicalName = "ATTR_PROD_MP_SalesRestrictions__TR_TR",
                    MappingHint = "", DefaultValue = "" },
                
                new() { DisplayName = "Ana Ürün Görseli", TechnicalName = "ATTR_PROD_MP_MainProductImage", MappingHint = "Image URL",
                    Mapping = new() { Field = ProductField.CdnImageUrl } },
                
                new() { DisplayName = "Ek Ürün Görseli_1", TechnicalName = "ATTR_PROD_MP_AdditionalImage1", MappingHint = "",
                    Mapping = new() { Field = ProductField.CdnAdditionalImage1 } },
                
                new() { DisplayName = "Ek Ürün Görseli_2", TechnicalName = "ATTR_PROD_MP_AdditionalImage2", MappingHint = "",
                    Mapping = new() { Field = ProductField.CdnAdditionalImage2 } },
                
                new() { DisplayName = "Ürün Detay Görünüm_1", TechnicalName = "ATTR_PROD_MP_DetailView1", MappingHint = "" },
                new() { DisplayName = "Ürün Detay Görünüm_2", TechnicalName = "ATTR_PROD_MP_DetailView2", MappingHint = "" },
                new() { DisplayName = "Ürün Detay Görünüm_3", TechnicalName = "ATTR_PROD_MP_DetailView3", MappingHint = "" },
                new() { DisplayName = "Lifestyle Görsel_1", TechnicalName = "ATTR_PROD_MP_LifeStyleImage1", MappingHint = "" },
                new() { DisplayName = "Lifestyle Görsel_2", TechnicalName = "ATTR_PROD_MP_LifeStyleImage2", MappingHint = "" },
                new() { DisplayName = "Lifestyle Görsel_3", TechnicalName = "ATTR_PROD_MP_LifeStyleImage3", MappingHint = "" },
                
                // Technical specs from Trendyol attributes
                new() { DisplayName = "Maksimum güç", TechnicalName = "PROD_FEAT_16246", MappingHint = "Güç (Watt)",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Güç" } },
                
                new() { DisplayName = "Gizli rezistans", TechnicalName = "PROD_FEAT_12330", MappingHint = "Gizli Rezistans",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Gizli Rezistans" } },
                
                new() { DisplayName = "Frekans", TechnicalName = "PROD_FEAT_14435", MappingHint = "Frekans",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Frekans" } },
                
                new() { DisplayName = "Giriþ Voltajý", TechnicalName = "PROD_FEAT_10449", MappingHint = "Voltaj",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Voltaj" } },
                
                new() { DisplayName = "Hacimsel kapasite", TechnicalName = "PROD_FEAT_10817", MappingHint = "Hacim",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Hacim" } },
                
                new() { DisplayName = "Otomatik Kapama", TechnicalName = "PROD_FEAT_10055", MappingHint = "Otomatik kapanma",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Otomatik Kapanma" } },
                
                new() { DisplayName = "Renk (temel)", TechnicalName = "PROD_FEAT_00003", MappingHint = "Renk",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Renk" } },
                
                new() { DisplayName = "Renk (Üreticiye Göre) (tr_TR)", TechnicalName = "PROD_FEAT_10812__TR_TR", MappingHint = "Renk",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Renk" } },
                
                new() { DisplayName = "Kasa Malzemesi (tr_TR)", TechnicalName = "PROD_FEAT_10986__TR_TR", MappingHint = "Materyal",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Materyal" } },
                
                new() { DisplayName = "Üretim Yeri (tr_TR)", TechnicalName = "PROD_FEAT_16042__TR_TR", MappingHint = "Menþei",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Menþei" } },
            }
        };
        
        _templates["trendyol_kettle"] = kettleTemplate;
    }

    public ExportTemplate? GetTemplate(string templateName)
    {
        return _templates.GetValueOrDefault(templateName);
    }

    public List<string> GetAvailableTemplates()
    {
        return _templates.Keys.ToList();
    }

    public Dictionary<string, string> GetTemplateInfo()
    {
        return _templates.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Description
        );
    }
}
