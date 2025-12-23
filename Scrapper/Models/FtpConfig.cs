namespace Scrapper.Models;

public class CdnFtpConfig
{
    public string Host { get; set; } = "4d2a1dbf530c769e.mncdn.com";
    public int Port { get; set; } = 21;
    public string Username { get; set; } = "mmstr_4579ba15";
    public string Password { get; set; } = "Ut123.#@";
    // Updated CDN base URL to match your actual CDN
    public string BaseUrl { get; set; } = "https://mmstr.sm.mncdn.com";
    public string RemotePath { get; set; } = "/images/products";
}
