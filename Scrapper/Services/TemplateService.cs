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
        
        // Trendyol Laptop Template (MediaMarkt)
        var laptopTemplate = new ExportTemplate
        {
            Name = "trendyol_laptop",
            Description = "MediaMarkt Laptop Template for Trendyol Products",
            Columns = new List<TemplateColumn>
            {
                new() { DisplayName = "Kategori", TechnicalName = "CATEGORY", MappingHint = "Category", 
                    DefaultValue = "", Mapping = new() { Field = ProductField.Category } },
                
                new() { DisplayName = "SHOP_SKU", TechnicalName = "SHOP_SKU", MappingHint = "SHOP_SKU",
                    DefaultValue = "" },
                
                new() { DisplayName = "Baþlýk", TechnicalName = "TITLE__TR_TR", MappingHint = "Product Name",
                    Mapping = new() { Field = ProductField.Name } },
                
                new() { DisplayName = "EAN", TechnicalName = "EAN", MappingHint = "Barcode",
                    Mapping = new() { Field = ProductField.Barcode } },
                
                new() { DisplayName = "Marka", TechnicalName = "BRAND", MappingHint = "Brand",
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
                
                // Laptop specific - Row 3 Trendyol attribute mappings from CSV
                new() { DisplayName = "RAM Tipi", TechnicalName = "PROD_FEAT_15969", MappingHint = "Ram (Sistem Belleði) Tipi",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Ram (Sistem Belleði) Tipi" } },
                
                new() { DisplayName = "RAM Bellek Boyutu", TechnicalName = "PROD_FEAT_11050", MappingHint = "Ram (Sistem Belleði)",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Ram (Sistem Belleði)" } },
                
                new() { DisplayName = "SSD", TechnicalName = "PROD_FEAT_15554", MappingHint = "SSD Kapasitesi",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "SSD Kapasitesi" } },
                
                new() { DisplayName = "Ekran Boyutu (inç)", TechnicalName = "PROD_FEAT_14112", MappingHint = "Ekran Boyutu",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Ekran Boyutu" } },
                
                new() { DisplayName = "Panel tipi", TechnicalName = "PROD_FEAT_14702", MappingHint = "Panel Tipi",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Panel Tipi" } },
                
                new() { DisplayName = "Dokunmatik ekran", TechnicalName = "PROD_FEAT_16168", MappingHint = "Dokunmatik Ekran",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Dokunmatik Ekran" } },
                
                new() { DisplayName = "Parmak Ýzi Sensörü", TechnicalName = "PROD_FEAT_16115", MappingHint = "Parmak Ýzi Okuyucu",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Parmak Ýzi Okuyucu" } },
                
                new() { DisplayName = "Renk (temel)", TechnicalName = "PROD_FEAT_00003", MappingHint = "Renk",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Renk" } },
                
                new() { DisplayName = "Ýþlemci Markasý", TechnicalName = "PROD_FEAT_10500", MappingHint = "Ýþlemci Tipi",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Ýþlemci Tipi" } },
                
                new() { DisplayName = "Aðýrlýk", TechnicalName = "PROD_FEAT_10826", MappingHint = "Cihaz Aðýrlýðý",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Cihaz Aðýrlýðý" } },
                
                new() { DisplayName = "Ýþletim Sistemi", TechnicalName = "PROD_FEAT_16077", MappingHint = "Ýþletim Sistemi",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Ýþletim Sistemi" } },
                
                new() { DisplayName = "Grafik Kartý", TechnicalName = "PROD_FEAT_16563", MappingHint = "Ekran Kartý",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Ekran Kartý" } },
                
                new() { DisplayName = "Ekran Yenileme Hýzý", TechnicalName = "PROD_FEAT_16763", MappingHint = "Ekran Yenileme Hýzý",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Ekran Yenileme Hýzý" } },
                
                new() { DisplayName = "Klavye Tipi (tr_TR)", TechnicalName = "PROD_FEAT_13228__TR_TR", MappingHint = "Klavye",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Klavye" } },
                
                new() { DisplayName = "Ýþlemci (tr_TR)", TechnicalName = "PROD_FEAT_11433__TR_TR", MappingHint = "Ýþlemci Modeli",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Ýþlemci Modeli" } },
                
                new() { DisplayName = "Üretim Yeri (tr_TR)", TechnicalName = "PROD_FEAT_16042__TR_TR", MappingHint = "Menþei",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Menþei" } },
                
                new() { DisplayName = "Baðlantýlar (tr_TR)", TechnicalName = "PROD_FEAT_10079__TR_TR", MappingHint = "Baðlantýlar",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Baðlantýlar" } },
                
                new() { DisplayName = "Çözünürlük (tr_TR)", TechnicalName = "PROD_FEAT_10990__TR_TR", MappingHint = "Çözünürlük",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Çözünürlük" } },
                
                new() { DisplayName = "Renk (Üreticiye Göre) (tr_TR)", TechnicalName = "PROD_FEAT_10812__TR_TR", MappingHint = "Renk",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Renk" } },
            }
        };
        
        _templates["trendyol_laptop"] = laptopTemplate;
        
        // Trendyol Robot Vacuum Template (MediaMarkt)
        var robotVacuumTemplate = new ExportTemplate
        {
            Name = "trendyol_robot_vacuum",
            Description = "MediaMarkt Robot Vacuum Template for Trendyol Products",
            Columns = new List<TemplateColumn>
            {
                new() { DisplayName = "Kategori", TechnicalName = "CATEGORY", MappingHint = "Category", 
                    DefaultValue = "", Mapping = new() { Field = ProductField.Category } },
                
                new() { DisplayName = "SHOP_SKU", TechnicalName = "SHOP_SKU", MappingHint = "SHOP_SKU",
                    DefaultValue = "" },
                
                new() { DisplayName = "Baþlýk", TechnicalName = "TITLE__TR_TR", MappingHint = "Product Name",
                    Mapping = new() { Field = ProductField.Name } },
                
                new() { DisplayName = "EAN", TechnicalName = "EAN", MappingHint = "Barcode",
                    Mapping = new() { Field = ProductField.Barcode } },
                
                new() { DisplayName = "Marka", TechnicalName = "BRAND", MappingHint = "Brand",
                    Mapping = new() { Field = ProductField.Brand } },
                
                new() { DisplayName = "Manufacturer Part Number (MPN)", TechnicalName = "ATTR_PROD_MP_Manufacturer_PartNumber", 
                    MappingHint = "", DefaultValue = "" },
                
                new() { DisplayName = "Ürün Açýklamasý", TechnicalName = "Product_Description__TR_TR", MappingHint = "Description",
                    Mapping = new() { Field = ProductField.Description } },
                
                new() { DisplayName = "Age Restriction (in years) (TR)", TechnicalName = "ATTR_PROD_MP_SalesRestrictions__TR_TR",
                    MappingHint = "", DefaultValue = "" },
                
                new() { DisplayName = "Ana Ürün Görseli", TechnicalName = "ATTR_PROD_MP_MainProductImage", MappingHint = "Main Image",
                    Mapping = new() { Field = ProductField.CdnImageUrl } },
                
                new() { DisplayName = "Ek Ürün Görseli_1", TechnicalName = "ATTR_PROD_MP_AdditionalImage1", MappingHint = "Additional Image 1",
                    Mapping = new() { Field = ProductField.CdnAdditionalImage1 } },
                
                new() { DisplayName = "Ek Ürün Görseli_2", TechnicalName = "ATTR_PROD_MP_AdditionalImage2", MappingHint = "Additional Image 2",
                    Mapping = new() { Field = ProductField.CdnAdditionalImage2 } },
                
                new() { DisplayName = "Ürün Detay Görünüm_1", TechnicalName = "ATTR_PROD_MP_DetailView1", MappingHint = "" },
                new() { DisplayName = "Ürün Detay Görünüm_2", TechnicalName = "ATTR_PROD_MP_DetailView2", MappingHint = "" },
                new() { DisplayName = "Ürün Detay Görünüm_3", TechnicalName = "ATTR_PROD_MP_DetailView3", MappingHint = "" },
                new() { DisplayName = "Lifestyle Görsel_1", TechnicalName = "ATTR_PROD_MP_LifeStyleImage1", MappingHint = "" },
                new() { DisplayName = "Lifestyle Görsel_2", TechnicalName = "ATTR_PROD_MP_LifeStyleImage2", MappingHint = "" },
                new() { DisplayName = "Lifestyle Görsel_3", TechnicalName = "ATTR_PROD_MP_LifeStyleImage3", MappingHint = "" },
                
                // Robot vacuum specific attributes
                new() { DisplayName = "Çöp Ýstasyonu", TechnicalName = "PROD_FEAT_15956", MappingHint = "Toz Ýstasyonu",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Toz Ýstasyonu" } },
                
                new() { DisplayName = "Uygulama Üzerinden Kontrol", TechnicalName = "PROD_FEAT_15742", MappingHint = "Uygulama ile uyumlu",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Uygulama ile uyumlu" } },
                
                new() { DisplayName = "Hazne Kapasitesi", TechnicalName = "PROD_FEAT_16257", MappingHint = "Maksimum toz haznesi hacmi",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Maksimum toz haznesi hacmi" } },
                
                new() { DisplayName = "Ses Seviyesi", TechnicalName = "PROD_FEAT_14906", MappingHint = "Ses Seviyesi",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Ses Seviyesi" } },
                
                new() { DisplayName = "Frekans", TechnicalName = "PROD_FEAT_14435", MappingHint = "Frekans",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Frekans" } },
                
                new() { DisplayName = "Voltaj", TechnicalName = "PROD_FEAT_10449", MappingHint = "Giriþ Voltajý",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Giriþ Voltajý" } },
                
                new() { DisplayName = "Yer Silme Özelliði", TechnicalName = "PROD_FEAT_11659", MappingHint = "Islak/kuru emme fonksiyonu",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Islak/kuru emme fonksiyonu" } },
                
                new() { DisplayName = "Þarjlý Kullaným Süresi", TechnicalName = "PROD_FEAT_11893__TR_TR", MappingHint = "Maksimum batarya süresi",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Maksimum batarya süresi" } },
                
                new() { DisplayName = "Menþei", TechnicalName = "PROD_FEAT_16042__TR_TR", MappingHint = "Menþei",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Menþei" } },
                
                new() { DisplayName = "Batarya kapasitesi", TechnicalName = "PROD_FEAT_16295", MappingHint = "Batarya kapasitesi",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Batarya kapasitesi" } },
                
                new() { DisplayName = "Engel algýlama", TechnicalName = "PROD_FEAT_16813", MappingHint = "Engel algýlama",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Engel algýlama" } },
                
                new() { DisplayName = "Halý algýlama", TechnicalName = "PROD_FEAT_16814", MappingHint = "Halý algýlama",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Halý algýlama" } },
                
                new() { DisplayName = "Lazer navigasyon", TechnicalName = "PROD_FEAT_16812", MappingHint = "Lazer navigasyon",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Lazer navigasyon" } },
                
                new() { DisplayName = "Otomatik Þarjý", TechnicalName = "PROD_FEAT_15971", MappingHint = "Otomatik Þarjý",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Otomatik Þarjý" } },
                
                new() { DisplayName = "Planlanmýþ temizlik", TechnicalName = "PROD_FEAT_15954", MappingHint = "Planlanmýþ temizlik",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Planlanmýþ temizlik" } },
                
                new() { DisplayName = "Maksimum emme gücü", TechnicalName = "PROD_FEAT_16253", MappingHint = "Maksimum emme gücü",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Maksimum emme gücü" } },
                
                new() { DisplayName = "WiFi", TechnicalName = "PROD_FEAT_10005", MappingHint = "Wi-Fi",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Wi-Fi" } },
                
                new() { DisplayName = "Renk", TechnicalName = "PROD_FEAT_00003", MappingHint = "Renk",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Renk" } },
                
                new() { DisplayName = "Aðýrlýk", TechnicalName = "PROD_FEAT_16333", MappingHint = "Aðýrlýk",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Aðýrlýk" } },
                
                new() { DisplayName = "Paspas Ýþlevi", TechnicalName = "PROD_FEAT_16766", MappingHint = "Paspas Ýþlevi",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Paspas Ýþlevi" } },
                
                new() { DisplayName = "Su haznesi kapasitesi", TechnicalName = "PROD_FEAT_10819", MappingHint = "Su haznesi kapasitesi",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Su haznesi kapasitesi" } },
                
                new() { DisplayName = "Kamera", TechnicalName = "PROD_FEAT_11318", MappingHint = "Kamera",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Kamera" } },
                
                new() { DisplayName = "Yýkanabilir filtre", TechnicalName = "PROD_FEAT_14504", MappingHint = "Yýkanabilir filtre",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Yýkanabilir filtre" } },
                
                new() { DisplayName = "Ses kontrolleri", TechnicalName = "PROD_FEAT_10500", MappingHint = "Ses kontrolleri",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Ses kontrolleri" } },
                
                new() { DisplayName = "Renk (Üreticiye Göre)", TechnicalName = "PROD_FEAT_10812__TR_TR", MappingHint = "Renk",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Renk" } },
                
                new() { DisplayName = "Þarj Süresi", TechnicalName = "PROD_FEAT_10080__TR_TR", MappingHint = "Þarj Süresi",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Þarj Süresi" } },
            }
        };
        
        _templates["trendyol_robot_vacuum"] = robotVacuumTemplate;
        
        // Trendyol Dryer Template (MediaMarkt)
        var dryerTemplate = new ExportTemplate
        {
            Name = "trendyol_dryer",
            Description = "MediaMarkt Dryer Template for Trendyol Products",
            Columns = new List<TemplateColumn>
            {
                // Row 3: "Category" - use ProductField.Category
                new() { DisplayName = "Kategori", TechnicalName = "CATEGORY", MappingHint = "Category", 
                    DefaultValue = "", Mapping = new() { Field = ProductField.Category } },
                
                // Row 3: "" - empty, no mapping
                new() { DisplayName = "SHOP_SKU", TechnicalName = "SHOP_SKU", MappingHint = "SHOP_SKU",
                    DefaultValue = "" },
                
                // Row 3: "Product Name" - use ProductField.Name
                new() { DisplayName = "Baþlýk", TechnicalName = "TITLE__TR_TR", MappingHint = "Product Name",
                    Mapping = new() { Field = ProductField.Name } },
                
                // Row 3: "Barcode" - use ProductField.Barcode
                new() { DisplayName = "EAN", TechnicalName = "EAN", MappingHint = "Barcode",
                    Mapping = new() { Field = ProductField.Barcode } },
                
                // Row 3: "Brand" - use ProductField.Brand
                new() { DisplayName = "Marka", TechnicalName = "BRAND", MappingHint = "Brand",
                    Mapping = new() { Field = ProductField.Brand } },
                
                // Row 3: "" - empty, no mapping
                new() { DisplayName = "Manufacturer Part Number (MPN)", TechnicalName = "ATTR_PROD_MP_Manufacturer_PartNumber", 
                    MappingHint = "", DefaultValue = "" },
                
                // Row 3: "Description" - use ProductField.Description
                new() { DisplayName = "Ürün Açýklamasý", TechnicalName = "Product_Description__TR_TR", MappingHint = "Description",
                    Mapping = new() { Field = ProductField.Description } },
                
                // Row 3: "" - empty, no mapping
                new() { DisplayName = "Age Restriction (in years) (TR)", TechnicalName = "ATTR_PROD_MP_SalesRestrictions__TR_TR",
                    MappingHint = "", DefaultValue = "" },
                
                // Row 3: "Image URL" - use ProductField.CdnImageUrl
                new() { DisplayName = "Ana Ürün Görseli", TechnicalName = "ATTR_PROD_MP_MainProductImage", MappingHint = "Main Image",
                    Mapping = new() { Field = ProductField.CdnImageUrl } },
                
                // Row 3: "Additional Images" - use ProductField.CdnAdditionalImage1
                new() { DisplayName = "Ek Ürün Görseli_1", TechnicalName = "ATTR_PROD_MP_AdditionalImage1", MappingHint = "Additional Image 1",
                    Mapping = new() { Field = ProductField.CdnAdditionalImage1 } },
                
                // Row 3: "" - empty, no mapping (but we can still use CdnAdditionalImage2)
                new() { DisplayName = "Ek Ürün Görseli_2", TechnicalName = "ATTR_PROD_MP_AdditionalImage2", MappingHint = "Additional Image 2",
                    Mapping = new() { Field = ProductField.CdnAdditionalImage2 } },
                
                new() { DisplayName = "Ürün Detay Görünüm_1", TechnicalName = "ATTR_PROD_MP_DetailView1", MappingHint = "" },
                new() { DisplayName = "Ürün Detay Görünüm_2", TechnicalName = "ATTR_PROD_MP_DetailView2", MappingHint = "" },
                new() { DisplayName = "Ürün Detay Görünüm_3", TechnicalName = "ATTR_PROD_MP_DetailView3", MappingHint = "" },
                new() { DisplayName = "Lifestyle Görsel_1", TechnicalName = "ATTR_PROD_MP_LifeStyleImage1", MappingHint = "" },
                new() { DisplayName = "Lifestyle Görsel_2", TechnicalName = "ATTR_PROD_MP_LifeStyleImage2", MappingHint = "" },
                new() { DisplayName = "Lifestyle Görsel_3", TechnicalName = "ATTR_PROD_MP_LifeStyleImage3", MappingHint = "" },
                
                // Empty columns - skip to column 44 (PROD_FEAT_15742)
                new() { DisplayName = "Installation Sketch 1 (TR)", TechnicalName = "ATTR_PROD_MP_InstallationSketch1__TR_TR", MappingHint = "" },
                new() { DisplayName = "Installation Sketch 2 (TR)", TechnicalName = "ATTR_PROD_MP_InstallationSketch2__TR_TR", MappingHint = "" },
                new() { DisplayName = "Enerji Etiketi", TechnicalName = "ATTR_PROD_MP_EnergyLabel__TR_TR", MappingHint = "" },
                new() { DisplayName = "Energy Datasheet (TR)", TechnicalName = "ATTR_PROD_MP_EnergyDataSheet__TR_TR", MappingHint = "" },
                new() { DisplayName = "Nutrition Table (TR)", TechnicalName = "ATTR_PROD_MP_NutritionTable__TR_TR", MappingHint = "" },
                new() { DisplayName = "Energy Label (EU2017/1369) (TR)", TechnicalName = "ATTR_PROD_MP_EnergyLabel_EU2017/1369__TR_TR", MappingHint = "" },
                new() { DisplayName = "Energy Datasheet (EU2017/1369) (TR)", TechnicalName = "ATTR_PROD_MP_EnergyDataSheet_EU2017/1369__TR_TR", MappingHint = "" },
                new() { DisplayName = "EPREL ID", TechnicalName = "ATTR_PROD_ENERGY_EPREL_ID", MappingHint = "" },
                new() { DisplayName = "Pazar Yeri Varyant Grup Kodu", TechnicalName = "ATTR_PROD_MP_VariantGroupCode", MappingHint = "" },
                new() { DisplayName = "Üretici Ticari Ünvaný", TechnicalName = "ATTR_PROD_MANU_LegalName", MappingHint = "" },
                new() { DisplayName = "Üretici Adresi: Þehir", TechnicalName = "ATTR_PROD_MANU_AddressCity", MappingHint = "" },
                new() { DisplayName = "Üretici Adresi: Ülke", TechnicalName = "ATTR_PROD_MANU_AddressCountry", MappingHint = "" },
                new() { DisplayName = "Üretici Adresi", TechnicalName = "ATTR_PROD_MANU_AddressDetails", MappingHint = "" },
                new() { DisplayName = "Manufacturer Address Line", TechnicalName = "ATTR_PROD_MANU_AddressLine", MappingHint = "" },
                new() { DisplayName = "Üretici Adresi _ Posta Kodu", TechnicalName = "ATTR_PROD_MANU_AddressZipCode", MappingHint = "" },
                new() { DisplayName = "Üretici Email", TechnicalName = "ATTR_PROD_MANU_Email", MappingHint = "" },
                new() { DisplayName = "Manufacturer Is EU Established", TechnicalName = "ATTR_PROD_MANU_EuEstablished", MappingHint = "" },
                new() { DisplayName = "Üretici Website URL", TechnicalName = "ATTR_PROD_MANU_WebsiteURL", MappingHint = "" },
                new() { DisplayName = "Safety Information Document (TR)", TechnicalName = "ATTR_PROD_MP_SafetyInformationDocument__TR_TR", MappingHint = "" },
                new() { DisplayName = "EU Responsible Person Name (TR)", TechnicalName = "ATTR_PROD_MP_MANU_ResponsiblePerson__TR_TR", MappingHint = "" },
                new() { DisplayName = "EU Responsible Person City (TR)", TechnicalName = "ATTR_PROD_MP_MANU_RPAddressCity__TR_TR", MappingHint = "" },
                new() { DisplayName = "EU Responsible Person Country (TR)", TechnicalName = "ATTR_PROD_MP_MANU_RPAddressCountry__TR_TR", MappingHint = "" },
                new() { DisplayName = "EU Responsible Person Address Additional Details (TR)", TechnicalName = "ATTR_PROD_MP_MANU_RPAddressDetails__TR_TR", MappingHint = "" },
                new() { DisplayName = "EU Responsible Person Address Line (TR)", TechnicalName = "ATTR_PROD_MP_MANU_RPAddressLine__TR_TR", MappingHint = "" },
                new() { DisplayName = "EU Responsible Person Zip Code (TR)", TechnicalName = "ATTR_PROD_MP_MANU_RPAddressZipCode__TR_TR", MappingHint = "" },
                new() { DisplayName = "EU Responsible Person Email (TR)", TechnicalName = "ATTR_PROD_MP_MANU_RPEmail__TR_TR", MappingHint = "" },
                new() { DisplayName = "EU Responsible Person Website URL (TR)", TechnicalName = "ATTR_PROD_MP_MANU_RPWebsiteURL__TR_TR", MappingHint = "" },
                
                // Row 3 is EMPTY for these columns (44-59), so NO Trendyol attribute names specified
                // We need to leave these without mapping or use the Row 1 display names
                new() { DisplayName = "Uygulama ile uyumlu", TechnicalName = "PROD_FEAT_15742", MappingHint = "" },
                new() { DisplayName = "Brushless motor", TechnicalName = "PROD_FEAT_15749", MappingHint = "" },
                new() { DisplayName = "Bluetooth", TechnicalName = "PROD_FEAT_15771", MappingHint = "" },
                new() { DisplayName = "Yükleme kapasitesi", TechnicalName = "PROD_FEAT_13839", MappingHint = "" },
                new() { DisplayName = "Ýç aydýnlatma", TechnicalName = "PROD_FEAT_11172", MappingHint = "" },
                new() { DisplayName = "Çocuklar  Emniyeti", TechnicalName = "PROD_FEAT_11350", MappingHint = "" },
                new() { DisplayName = "Kýrýþýklýk önleyici sistem", TechnicalName = "PROD_FEAT_11361", MappingHint = "" },
                new() { DisplayName = "Kalan süre ekraný", TechnicalName = "PROD_FEAT_11860", MappingHint = "" },
                new() { DisplayName = "Kendinden temizlemeli", TechnicalName = "PROD_FEAT_11967", MappingHint = "" },
                new() { DisplayName = "Nem sensörü", TechnicalName = "PROD_FEAT_11976", MappingHint = "" },
                new() { DisplayName = "Stackable", TechnicalName = "PROD_FEAT_11986", MappingHint = "" },
                new() { DisplayName = "Maksimum devir hýzý", TechnicalName = "PROD_FEAT_11905", MappingHint = "" },
                new() { DisplayName = "Sinyal Kelimesi", TechnicalName = "PROD_FEAT_18202", MappingHint = "" },
                new() { DisplayName = "Risk Grubu Kodlarý", TechnicalName = "PROD_FEAT_18200", MappingHint = "" },
                new() { DisplayName = "Labeling requirement", TechnicalName = "PROD_FEAT_18130", MappingHint = "" },
                
                // Row 3: "Enerji Sýnýf Aralýðý" - THIS IS THE KEY ONE!
                new() { DisplayName = "AB Enerji Verimlilik Sýnýfý", TechnicalName = "PROD_FEAT_18135", MappingHint = "Enerji Sýnýf Aralýðý",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Enerji Sýnýf Aralýðý" } },
                
                // More empty columns
                new() { DisplayName = "Ütü istemeyen parçalar' kurutma programýnýn harcadýðý enerji", TechnicalName = "PROD_FEAT_90025", MappingHint = "" },
                new() { DisplayName = "Kuru ütülenen pamuklular' kurutma programýnýn harcadýðý enerji", TechnicalName = "PROD_FEAT_90024", MappingHint = "" },
                new() { DisplayName = "Pamuklularý' kurutma programýnýn harcadýðý enerji", TechnicalName = "PROD_FEAT_90023", MappingHint = "" },
                new() { DisplayName = "Ütü istemeyen parçalar' kurutma programýnýn kapasitesi", TechnicalName = "PROD_FEAT_90028", MappingHint = "" },
                new() { DisplayName = "Kuru ütülenen pamuklular' kurutma programýnýn kapasitesi", TechnicalName = "PROD_FEAT_90027", MappingHint = "" },
                new() { DisplayName = "Pamuklularý' kurutma programýnýn kapasitesi", TechnicalName = "PROD_FEAT_90026", MappingHint = "" },
                new() { DisplayName = "Ütü istemeyen parçalarýn' kurutma süresi", TechnicalName = "PROD_FEAT_90034", MappingHint = "" },
                new() { DisplayName = "Kuru ütülenen pamukluluarýn' kurutma süresi", TechnicalName = "PROD_FEAT_90033", MappingHint = "" },
                new() { DisplayName = "Pamuklularýn' kurutma süresi", TechnicalName = "PROD_FEAT_90032", MappingHint = "" },
                new() { DisplayName = "Ütü istemeyen parçalar' kurutma programýnýn harcadýðý su", TechnicalName = "PROD_FEAT_90031", MappingHint = "" },
                new() { DisplayName = "Kuru ütülenen pamuklular' kurutma programýnýn harcadýðý su", TechnicalName = "PROD_FEAT_90030", MappingHint = "" },
                new() { DisplayName = "Gömme tip aygýt", TechnicalName = "PROD_FEAT_90007", MappingHint = "" },
                new() { DisplayName = "Çevre Dostu", TechnicalName = "PROD_FEAT_18889", MappingHint = "" },
                new() { DisplayName = "Gürültü Seviyesi", TechnicalName = "PROD_FEAT_90096", MappingHint = "" },
                new() { DisplayName = "Yoðunlaþtýrma Verimliliði Sýnýfý", TechnicalName = "PROD_FEAT_90095", MappingHint = "" },
                new() { DisplayName = "Kýsmi Yükte Standart Pamuk Program Süresi", TechnicalName = "PROD_FEAT_90094", MappingHint = "" },
                new() { DisplayName = "Kýsmý Yükte Standart Pamuk Program Süresi", TechnicalName = "PROD_FEAT_90093", MappingHint = "" },
                new() { DisplayName = "Bekleme modunda enerji tüketimi", TechnicalName = "PROD_FEAT_90092", MappingHint = "" },
                new() { DisplayName = "Kapalý mod durumunda enerji tüketimi", TechnicalName = "PROD_FEAT_90091", MappingHint = "" },
                new() { DisplayName = "Kýsmi Yükte Enerji Tüketimi", TechnicalName = "PROD_FEAT_90090", MappingHint = "" },
                new() { DisplayName = "Tam Yükte Enerji Tüketimi", TechnicalName = "PROD_FEAT_90089", MappingHint = "" },
                new() { DisplayName = "Kurutma Süreci", TechnicalName = "PROD_FEAT_90088", MappingHint = "" },
                new() { DisplayName = "Yýllýk enerji tüketimi", TechnicalName = "PROD_FEAT_90085", MappingHint = "" },
                new() { DisplayName = "Kapasite", TechnicalName = "PROD_FEAT_90082", MappingHint = "" },
                
                // Row 3: "Yükseklik", "Geniþlik", "Derinlik" - THESE ARE KEY!
                new() { DisplayName = "Yükseklik", TechnicalName = "PROD_FEAT_16111", MappingHint = "Yükseklik",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Yükseklik" } },
                
                new() { DisplayName = "Geniþlik", TechnicalName = "PROD_FEAT_16110", MappingHint = "Geniþlik",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Geniþlik" } },
                
                new() { DisplayName = "Derinlik", TechnicalName = "PROD_FEAT_16112", MappingHint = "Derinlik",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Derinlik" } },
                
                // More columns continue...
                new() { DisplayName = "Program sayýsý", TechnicalName = "PROD_FEAT_16136", MappingHint = "" },
                new() { DisplayName = "Yünlü Sepeti", TechnicalName = "PROD_FEAT_16603", MappingHint = "" },
                new() { DisplayName = "NFC (Yakýn Alan) Desteði", TechnicalName = "PROD_FEAT_14004", MappingHint = "" },
                new() { DisplayName = "Tam yükte standart pamuklu programýnýn ortalama yoðunlaþma verimliliði", TechnicalName = "PROD_FEAT_16500", MappingHint = "" },
                new() { DisplayName = "Kýsmi yükte standart pamuklu programýnýn ortalama yoðunlaþma verimliliði", TechnicalName = "PROD_FEAT_16501", MappingHint = "" },
                new() { DisplayName = "Akýllý Ev Alaný", TechnicalName = "PROD_FEAT_16517", MappingHint = "" },
                new() { DisplayName = "Tersine fonksiyon", TechnicalName = "PROD_FEAT_14526", MappingHint = "" },
                new() { DisplayName = "Tekerlekler", TechnicalName = "PROD_FEAT_14527", MappingHint = "" },
                new() { DisplayName = "Filtre kontrol göstergesi", TechnicalName = "PROD_FEAT_14433", MappingHint = "" },
                new() { DisplayName = "Frekans", TechnicalName = "PROD_FEAT_14435", MappingHint = "" },
                new() { DisplayName = "Kontrol göstergesi, boþ hazne", TechnicalName = "PROD_FEAT_14470", MappingHint = "" },
                new() { DisplayName = "Ayarlanabilir ayaklar", TechnicalName = "PROD_FEAT_14451", MappingHint = "" },
                new() { DisplayName = "Ayarlanabilir ayaklar, maksimum", TechnicalName = "PROD_FEAT_14453", MappingHint = "" },
                new() { DisplayName = "Güç kablosunun uzunluðu", TechnicalName = "PROD_FEAT_14482", MappingHint = "" },
                new() { DisplayName = "Buhar deliði çapý", TechnicalName = "PROD_FEAT_14402", MappingHint = "" },
                new() { DisplayName = "Ortalama kurutma süresi ( Sadece bunu yazalým)", TechnicalName = "PROD_FEAT_14405", MappingHint = "" },
                new() { DisplayName = "Ortalama kurutma süresi, kurutulmuþ pamuklular, %70 kalýntý nem (800 devir)", TechnicalName = "PROD_FEAT_14406", MappingHint = "" },
                new() { DisplayName = "Ortalama kurutma süresi, kurutulmuþ ütü istemeyen parçalar %50 kalýntý nem (1000 devir)", TechnicalName = "PROD_FEAT_14407", MappingHint = "" },
                new() { DisplayName = "Ortalama kurutma süresi, nemli ütülenen pamuklular, %70 kalýntý nem (800 devir)", TechnicalName = "PROD_FEAT_14408", MappingHint = "" },
                new() { DisplayName = "Yedek", TechnicalName = "PROD_FEAT_14339", MappingHint = "" },
                new() { DisplayName = "Sökülebilir mutfak tezgahý", TechnicalName = "PROD_FEAT_14363", MappingHint = "" },
                new() { DisplayName = "Buhar deliði", TechnicalName = "PROD_FEAT_14396", MappingHint = "" },
                new() { DisplayName = "Ambalaj Geniþliði", TechnicalName = "PROD_FEAT_14700", MappingHint = "" },
                new() { DisplayName = "Ambalaj Yüksekliði", TechnicalName = "PROD_FEAT_14701", MappingHint = "" },
                new() { DisplayName = "Ambalaj Derinliði", TechnicalName = "PROD_FEAT_14702", MappingHint = "" },
                new() { DisplayName = "Ambalajlý Aðýrlýk", TechnicalName = "PROD_FEAT_14704", MappingHint = "" },
                new() { DisplayName = "Ortalama kurutma süresi, kuru ütülenen pamuklular, %50 kalýntý nem (1400 devir)", TechnicalName = "PROD_FEAT_14600", MappingHint = "" },
                new() { DisplayName = "Kurutma sýcaklýðý, seçilebilir", TechnicalName = "PROD_FEAT_14562", MappingHint = "" },
                new() { DisplayName = "Kurutma süresi seçimi", TechnicalName = "PROD_FEAT_14563", MappingHint = "" },
                new() { DisplayName = "Tambur hacmi", TechnicalName = "PROD_FEAT_14564", MappingHint = "" },
                new() { DisplayName = "Tambur hacmi", TechnicalName = "PROD_FEAT_14558", MappingHint = "" },
                new() { DisplayName = "Kapýlarý açýkken ürün derinliði (90°)", TechnicalName = "PROD_FEAT_14585", MappingHint = "" },
                new() { DisplayName = "Su tahliye borusu", TechnicalName = "PROD_FEAT_12236", MappingHint = "" },
                new() { DisplayName = "Programýn sonu göstergesi", TechnicalName = "PROD_FEAT_12253", MappingHint = "" },
                new() { DisplayName = "Kapý tamponu", TechnicalName = "PROD_FEAT_12120", MappingHint = "" },
                new() { DisplayName = "Kontrol tipi", TechnicalName = "PROD_FEAT_12484", MappingHint = "" },
                new() { DisplayName = "Buhar fonksiyonu", TechnicalName = "PROD_FEAT_10521", MappingHint = "" },
                new() { DisplayName = "WÝFÝ", TechnicalName = "PROD_FEAT_10576", MappingHint = "" },
                new() { DisplayName = "Giriþ Voltajý", TechnicalName = "PROD_FEAT_10449", MappingHint = "" },
                
                // Row 3: "Enerji Sýnýfý" - KEY!
                new() { DisplayName = "Enerji verimlilik sýnýfý", TechnicalName = "PROD_FEAT_10770", MappingHint = "Enerji Sýnýfý",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Enerji Sýnýfý" } },
                
                new() { DisplayName = "Yerleþtir", TechnicalName = "PROD_FEAT_10714", MappingHint = "" },
                new() { DisplayName = "Doldurma miktarý, pamuklular (kuru)", TechnicalName = "PROD_FEAT_10907", MappingHint = "" },
                new() { DisplayName = "Bekleme modu süresi", TechnicalName = "PROD_FEAT_91041", MappingHint = "" },
                new() { DisplayName = "Üretici tarafýndan desteklenen yazýlým güncellemeleri", TechnicalName = "PROD_FEAT_16743", MappingHint = "" },
                new() { DisplayName = "Ürün tanýtým tarihi", TechnicalName = "PROD_FEAT_16746", MappingHint = "" },
                new() { DisplayName = "Model yýlý", TechnicalName = "PROD_FEAT_16617", MappingHint = "" },
                
                // Row 3: "Renk" - KEY!
                new() { DisplayName = "Renk (temel)", TechnicalName = "PROD_FEAT_00003", MappingHint = "Renk",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Renk" } },
                
                new() { DisplayName = "Aðýrlýk", TechnicalName = "PROD_FEAT_16333", MappingHint = "" },
                new() { DisplayName = "Desteklenen yazýlým güncellemelerinin minumum süresi", TechnicalName = "PROD_FEAT_16744", MappingHint = "" },
                new() { DisplayName = "Programlama sinyalinin sonu", TechnicalName = "PROD_FEAT_14545", MappingHint = "" },
                new() { DisplayName = "Önlem Açýlamalarý", TechnicalName = "PROD_FEAT_18203", MappingHint = "" },
                new() { DisplayName = "Yenilenmiþ", TechnicalName = "PROD_FEAT_10500", MappingHint = "" },
                new() { DisplayName = "Çýkýntýlý elementlerle derinlik", TechnicalName = "PROD_FEAT_16908", MappingHint = "" },
                new() { DisplayName = "Yapay Zeka", TechnicalName = "PROD_FEAT_16914", MappingHint = "" },
                new() { DisplayName = "Kullanýcý bilgilerinin paylaþýmýný gerektirir", TechnicalName = "PROD_FEAT_16919", MappingHint = "" },
                new() { DisplayName = "Yapay Zeka Ýþlevleri", TechnicalName = "PROD_FEAT_16943", MappingHint = "" },
                new() { DisplayName = "Markaya özgü özellikler", TechnicalName = "PROD_FEAT_16962", MappingHint = "" },
                new() { DisplayName = "Plug type (tr_TR)", TechnicalName = "PROD_FEAT_15985__TR_TR", MappingHint = "" },
                new() { DisplayName = "Özel Nitelikler (tr_TR)", TechnicalName = "PROD_FEAT_13366__TR_TR", MappingHint = "" },
                new() { DisplayName = "Ürün belgeleri (tr_TR)", TechnicalName = "PROD_FEAT_13648__TR_TR", MappingHint = "" },
                new() { DisplayName = "Güvenlik özellikleri (tr_TR)", TechnicalName = "PROD_FEAT_13676__TR_TR", MappingHint = "" },
                new() { DisplayName = "Aðýrlýk (Üreticiye Göre) (tr_TR)", TechnicalName = "PROD_FEAT_11011__TR_TR", MappingHint = "" },
                new() { DisplayName = "Kazan malzemesi (tr_TR)", TechnicalName = "PROD_FEAT_13710__TR_TR", MappingHint = "" },
                new() { DisplayName = "Keywords (tr_TR)", TechnicalName = "PROD_FEAT_11111__TR_TR", MappingHint = "" },
                new() { DisplayName = "Isýtma sistemi (tr_TR)", TechnicalName = "PROD_FEAT_11086__TR_TR", MappingHint = "" },
                new() { DisplayName = "Kutu Ýçeriði (tr_TR)", TechnicalName = "PROD_FEAT_11470__TR_TR", MappingHint = "" },
                new() { DisplayName = "Soðutucu Gazý (tr_TR)", TechnicalName = "PROD_FEAT_11314__TR_TR", MappingHint = "" },
                new() { DisplayName = "Programlar (tr_TR)", TechnicalName = "PROD_FEAT_11779__TR_TR", MappingHint = "" },
                new() { DisplayName = "Series (tr_TR)", TechnicalName = "PROD_FEAT_18008__TR_TR", MappingHint = "" },
                
                // Row 3: "Menþei" - KEY!
                new() { DisplayName = "Üretim Yeri (tr_TR)", TechnicalName = "PROD_FEAT_16042__TR_TR", MappingHint = "Menþei",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Menþei" } },
                
                // More columns
                new() { DisplayName = "'Pamuklularý' kurutma programýnýn harcadýðý su (tr_TR)", TechnicalName = "PROD_FEAT_90029__TR_TR", MappingHint = "" },
                new() { DisplayName = "Ortalama yýllýk enerji/su tüketimi (tr_TR)", TechnicalName = "PROD_FEAT_90035__TR_TR", MappingHint = "" },
                new() { DisplayName = "Yasal metin (tr_TR)", TechnicalName = "PROD_FEAT_16005__TR_TR", MappingHint = "" },
                new() { DisplayName = "Model adý/tanýmý (tr_TR)", TechnicalName = "PROD_FEAT_90068__TR_TR", MappingHint = "" },
                new() { DisplayName = "Yýllýk enerji tüketimi gaz ýsýtmalý, elektrik bileþeni (tr_TR)", TechnicalName = "PROD_FEAT_90087__TR_TR", MappingHint = "" },
                new() { DisplayName = "Yýllýk enerji tüketimi gaz ýsýtmalý (tr_TR)", TechnicalName = "PROD_FEAT_90086__TR_TR", MappingHint = "" },
                new() { DisplayName = "Uyumluluk (tr_TR)", TechnicalName = "PROD_FEAT_16518__TR_TR", MappingHint = "" },
                new() { DisplayName = "Uyumlu cihazlar (tr_TR)", TechnicalName = "PROD_FEAT_16920__TR_TR", MappingHint = "" },
                new() { DisplayName = "Cihaz kullanýmý için gerekli uygulama/servis (tr_TR)", TechnicalName = "PROD_FEAT_16918__TR_TR", MappingHint = "" },
                new() { DisplayName = "Ek güncelleme bilgileri (tr_TR)", TechnicalName = "PROD_FEAT_16745__TR_TR", MappingHint = "" },
                new() { DisplayName = "Politika güncelleþtirme (tr_TR)", TechnicalName = "PROD_FEAT_16747__TR_TR", MappingHint = "" },
                new() { DisplayName = "Baca Hortum uzunluðu (tr_TR)", TechnicalName = "PROD_FEAT_14481__TR_TR", MappingHint = "" },
                new() { DisplayName = "Kontrol unsurlarý (tr_TR)", TechnicalName = "PROD_FEAT_14384__TR_TR", MappingHint = "" },
                new() { DisplayName = "Ayarlanabilir baþlangýç zamaný (tr_TR)", TechnicalName = "PROD_FEAT_12104__TR_TR", MappingHint = "" },
                new() { DisplayName = "Ekrandan seçilebilen diller (tr_TR)", TechnicalName = "PROD_FEAT_14583__TR_TR", MappingHint = "" },
                new() { DisplayName = "Kýyaslama programý (tr_TR)", TechnicalName = "PROD_FEAT_14576__TR_TR", MappingHint = "" },
                new() { DisplayName = "Baðlanmýþ yük (tr_TR)", TechnicalName = "PROD_FEAT_10139__TR_TR", MappingHint = "" },
                new() { DisplayName = "Yapý Þekli (tr_TR)", TechnicalName = "PROD_FEAT_10401__TR_TR", MappingHint = "" },
                new() { DisplayName = "Kullaným (tr_TR)", TechnicalName = "PROD_FEAT_10410__TR_TR", MappingHint = "" },
                new() { DisplayName = "Ekran (tr_TR)", TechnicalName = "PROD_FEAT_10649__TR_TR", MappingHint = "" },
                new() { DisplayName = "Ürün Tipi (tr_TR)", TechnicalName = "PROD_FEAT_10990__TR_TR", MappingHint = "" },
                
                // Row 3: "Renk" - KEY!
                new() { DisplayName = "Renk (Üreticiye Göre) (tr_TR)", TechnicalName = "PROD_FEAT_10812__TR_TR", MappingHint = "Renk",
                    Mapping = new() { Field = ProductField.DynamicAttribute, AttributeKey = "Renk" } },
                
                new() { DisplayName = "Filtre sistemleri (tr_TR)", TechnicalName = "PROD_FEAT_10845__TR_TR", MappingHint = "" },
                new() { DisplayName = "Otomatik programlar (tr_TR)", TechnicalName = "PROD_FEAT_15273__TR_TR", MappingHint = "" },
                new() { DisplayName = "Test Sonucu (tr_TR)", TechnicalName = "PROD_FEAT_15550__TR_TR", MappingHint = "" },
                
                // Rest of EU energy columns (all empty in row 3)
                new() { DisplayName = "Gecikmeli baþlatmada enerji tüketimi", TechnicalName = "PROD_FEAT_91345", MappingHint = "" },
                new() { DisplayName = "Saat:dakika cinsinden tam yükte eko program süresi", TechnicalName = "PROD_FEAT_91344", MappingHint = "" },
                new() { DisplayName = "Tam yükte eko program için kg cinsinden nominal kapasite", TechnicalName = "PROD_FEAT_91343", MappingHint = "" },
                new() { DisplayName = "Her 100 kurutma döngüsü için kWh cinsinden aðýrlýklý ortalama enerji tüketimi (gaz ve elektrik)", TechnicalName = "PROD_FEAT_91342", MappingHint = "" },
                new() { DisplayName = "Enerji Verimliliði Sýnýfý (EU 2017/1369)", TechnicalName = "PROD_FEAT_91100", MappingHint = "" },
                new() { DisplayName = "Yoðuþma verimliliði sýnýfý (EU 2017/1369)", TechnicalName = "PROD_FEAT_91341", MappingHint = "" },
                new() { DisplayName = "Eko programýn kurutma döngüsünün dB(A) cinsinden akustik hava kaynaklý gürültü emisyonu", TechnicalName = "PROD_FEAT_91340", MappingHint = "" },
                new() { DisplayName = "Weighted energy consumption per 100 cycles in kWh", TechnicalName = "PROD_FEAT_91306", MappingHint = "" },
                new() { DisplayName = "Yoðuþma verimliliði", TechnicalName = "PROD_FEAT_91349", MappingHint = "" },
                new() { DisplayName = "Kapalý modda enerji tüketimi", TechnicalName = "PROD_FEAT_91348", MappingHint = "" },
                new() { DisplayName = "Bekleme modunda enerji tüketimi", TechnicalName = "PROD_FEAT_91347", MappingHint = "" },
                new() { DisplayName = "Aða baðlý bekleme modunda enerji tüketimi", TechnicalName = "PROD_FEAT_91346", MappingHint = "" },
                new() { DisplayName = "Eko programýnýn yarým yükte program süresi", TechnicalName = "PROD_FEAT_91356", MappingHint = "" },
                new() { DisplayName = "Eko programýnýn standart enerji tüketimi (çevrim baþýna)", TechnicalName = "PROD_FEAT_91355", MappingHint = "" },
                new() { DisplayName = "Eko programýnýn aðýrlýklý enerji tüketimi (çevrim baþýna)", TechnicalName = "PROD_FEAT_91354", MappingHint = "" },
                new() { DisplayName = "Enerji Verimliliði Ýndeksi (EEI)", TechnicalName = "PROD_FEAT_91156", MappingHint = "" },
                new() { DisplayName = "Eko programýnýn yarým yükte yardýmcý elektrik tüketimi", TechnicalName = "PROD_FEAT_91353", MappingHint = "" },
                new() { DisplayName = "Model Tanýmlayýcý (tr_TR)", TechnicalName = "PROD_FEAT_91110__TR_TR", MappingHint = "" },
                new() { DisplayName = "Eko programýnýn tam yükte yardýmcý elektrik tüketimi", TechnicalName = "PROD_FEAT_91352", MappingHint = "" },
                new() { DisplayName = "Eko programýnýn yarým yükte gaz tüketimi", TechnicalName = "PROD_FEAT_91351", MappingHint = "" },
                new() { DisplayName = "Eko programýnýn tam yükte gaz tüketimi", TechnicalName = "PROD_FEAT_91350", MappingHint = "" },
                new() { DisplayName = "Eko programýnýn tam yükte enerji tüketimi", TechnicalName = "PROD_FEAT_91359", MappingHint = "" },
                new() { DisplayName = "Eko programýnýn yarým yükte enerji tüketimi", TechnicalName = "PROD_FEAT_91358", MappingHint = "" },
                new() { DisplayName = "Eko programýnýn aðýrlýklý program süresi", TechnicalName = "PROD_FEAT_91357", MappingHint = "" },
                new() { DisplayName = "Eko programýnýn aðýrlýklý yoðuþma verimliliði", TechnicalName = "PROD_FEAT_91363", MappingHint = "" },
                new() { DisplayName = "Eko programýnýn yarým yükte ortalama yoðuþma verimliliði", TechnicalName = "PROD_FEAT_91362", MappingHint = "" },
                new() { DisplayName = "Eko programýnýn tam yükte ortalama yoðuþma verimliliði", TechnicalName = "PROD_FEAT_91361", MappingHint = "" },
                new() { DisplayName = "«Bekleme modu» bilgi gösterimini içeriyor mu?", TechnicalName = "PROD_FEAT_91360", MappingHint = "" },
                new() { DisplayName = "Saat Cinsinden Program Süresi (Yarým)", TechnicalName = "PROD_FEAT_91129", MappingHint = "" },
                new() { DisplayName = "Saat Cinsinden Program Süresi (Nominal Kapasite)", TechnicalName = "PROD_FEAT_91128", MappingHint = "" },
                new() { DisplayName = "KWh Cinsinden Enerji Tüketimi (Döngü Baþýna)", TechnicalName = "PROD_FEAT_91253", MappingHint = "" },
                new() { DisplayName = "Type", TechnicalName = "PROD_FEAT_91131", MappingHint = "" },
                new() { DisplayName = "Eko programýnýn kurutma döngüsünün akustik hava kaynaklý gürültü emisyon sýnýfý (EU 2017/1369)", TechnicalName = "PROD_FEAT_91339", MappingHint = "" },
                new() { DisplayName = "Gerçek zamanlý veri üretimi (tr_TR)", TechnicalName = "PROD_FEAT_94001__TR_TR", MappingHint = "" },
                new() { DisplayName = "Oluþturulan verinin biçimi, türü ve hacmi (tr_TR)", TechnicalName = "PROD_FEAT_94000__TR_TR", MappingHint = "" },
                new() { DisplayName = "Veri saklama süresi (tr_TR)", TechnicalName = "PROD_FEAT_94003__TR_TR", MappingHint = "" },
                new() { DisplayName = "Veri depolama yeteneði (tr_TR)", TechnicalName = "PROD_FEAT_94002__TR_TR", MappingHint = "" },
                new() { DisplayName = "AB Veri Yasasý'na göre daha fazla bilgi (tr_TR)", TechnicalName = "PROD_FEAT_94005__TR_TR", MappingHint = "" },
                new() { DisplayName = "Kullanýcý için veri eriþilebilirliði (tr_TR)", TechnicalName = "PROD_FEAT_94004__TR_TR", MappingHint = "" },
                new() { DisplayName = "Üretici bilgilerine baðlantý", TechnicalName = "PROD_FEAT_94006", MappingHint = "" },
                new() { DisplayName = "Kapak malzemesi (tr_TR)", TechnicalName = "PROD_FEAT_14499__TR_TR", MappingHint = "" },
                new() { DisplayName = "Sustainability", TechnicalName = "PROD_FEAT_13649", MappingHint = "" },
                new() { DisplayName = "Malzeme (tr_TR)", TechnicalName = "PROD_FEAT_11514__TR_TR", MappingHint = "" },
                new() { DisplayName = "Kasa Malzemesi (tr_TR)", TechnicalName = "PROD_FEAT_10986__TR_TR", MappingHint = "" },
                new() { DisplayName = "Üretici Garantisi", TechnicalName = "PROD_FEAT_16207", MappingHint = "" },
            }
        };
        
        _templates["trendyol_dryer"] = dryerTemplate;
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
