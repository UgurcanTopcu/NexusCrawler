using System.Net;

namespace Scrapper.Services;

/// <summary>
/// Simple FTP connection tester to diagnose issues
/// </summary>
public class FtpConnectionTester
{
    public static void TestConnection()
    {
        Console.WriteLine("\n=== FTP CONNECTION TEST ===\n");
        
        var host = "4d2a1dbf530c769e.mncdn.com";
        var username = "mmstr_4579ba15";
        var password = "Ut123.#@";
        
        // Test 1: List root directory
        Console.WriteLine("Test 1: Listing root directory...");
        try
        {
            var request = (FtpWebRequest)WebRequest.Create($"ftp://{host}/");
            request.Method = WebRequestMethods.Ftp.ListDirectory;
            request.Credentials = new NetworkCredential(username, password);
            request.UsePassive = true;
            request.KeepAlive = false;
            request.Timeout = 10000;

            using (var response = (FtpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream()))
            {
                Console.WriteLine($"? Connected! Status: {response.StatusDescription}");
                Console.WriteLine("Directory contents:");
                Console.WriteLine(reader.ReadToEnd());
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"? Failed: {ex.Message}");
        }

        // Test 2: Check if /images exists
        Console.WriteLine("\nTest 2: Checking /images directory...");
        try
        {
            var request = (FtpWebRequest)WebRequest.Create($"ftp://{host}/images");
            request.Method = WebRequestMethods.Ftp.ListDirectory;
            request.Credentials = new NetworkCredential(username, password);
            request.UsePassive = true;
            request.KeepAlive = false;
            request.Timeout = 10000;

            using (var response = (FtpWebResponse)request.GetResponse())
            {
                Console.WriteLine($"? /images exists! Status: {response.StatusDescription}");
            }
        }
        catch (WebException ex)
        {
            if (ex.Response is FtpWebResponse ftpResp && ftpResp.StatusCode == FtpStatusCode.ActionNotTakenFileUnavailable)
            {
                Console.WriteLine("? /images directory doesn't exist - need to create it");
            }
            else
            {
                Console.WriteLine($"? Failed: {ex.Message}");
            }
        }

        // Test 3: Check if /images/products exists
        Console.WriteLine("\nTest 3: Checking /images/products directory...");
        try
        {
            var request = (FtpWebRequest)WebRequest.Create($"ftp://{host}/images/products");
            request.Method = WebRequestMethods.Ftp.ListDirectory;
            request.Credentials = new NetworkCredential(username, password);
            request.UsePassive = true;
            request.KeepAlive = false;
            request.Timeout = 10000;

            using (var response = (FtpWebResponse)request.GetResponse())
            {
                Console.WriteLine($"? /images/products exists! Status: {response.StatusDescription}");
            }
        }
        catch (WebException ex)
        {
            if (ex.Response is FtpWebResponse ftpResp && ftpResp.StatusCode == FtpStatusCode.ActionNotTakenFileUnavailable)
            {
                Console.WriteLine("? /images/products directory doesn't exist - need to create it");
            }
            else
            {
                Console.WriteLine($"? Failed: {ex.Message}");
            }
        }

        // Test 4: Try creating a test file with PASSIVE mode
        Console.WriteLine("\nTest 4: Uploading test file (PASSIVE mode)...");
        try
        {
            var testData = System.Text.Encoding.UTF8.GetBytes("test");
            var request = (FtpWebRequest)WebRequest.Create($"ftp://{host}/test_passive.txt");
            request.Method = WebRequestMethods.Ftp.UploadFile;
            request.Credentials = new NetworkCredential(username, password);
            request.UsePassive = true;
            request.KeepAlive = false;
            request.UseBinary = true;
            request.ContentLength = testData.Length;
            request.Timeout = 10000;

            using (var stream = request.GetRequestStream())
            {
                stream.Write(testData, 0, testData.Length);
            }

            using (var response = (FtpWebResponse)request.GetResponse())
            {
                Console.WriteLine($"? Upload successful (PASSIVE)! Status: {response.StatusDescription}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"? PASSIVE mode failed: {ex.Message}");
        }

        // Test 5: Try creating a test file with ACTIVE mode
        Console.WriteLine("\nTest 5: Uploading test file (ACTIVE mode)...");
        try
        {
            var testData = System.Text.Encoding.UTF8.GetBytes("test");
            var request = (FtpWebRequest)WebRequest.Create($"ftp://{host}/test_active.txt");
            request.Method = WebRequestMethods.Ftp.UploadFile;
            request.Credentials = new NetworkCredential(username, password);
            request.UsePassive = false; // ACTIVE mode
            request.KeepAlive = false;
            request.UseBinary = true;
            request.ContentLength = testData.Length;
            request.Timeout = 10000;

            using (var stream = request.GetRequestStream())
            {
                stream.Write(testData, 0, testData.Length);
            }

            using (var response = (FtpWebResponse)request.GetResponse())
            {
                Console.WriteLine($"? Upload successful (ACTIVE)! Status: {response.StatusDescription}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"? ACTIVE mode failed: {ex.Message}");
        }

        Console.WriteLine("\n=== TEST COMPLETE ===\n");
    }
}
