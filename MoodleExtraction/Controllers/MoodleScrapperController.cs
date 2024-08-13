using HtmlAgilityPack;
using Microsoft.AspNetCore.Mvc;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SeleniumExtras.WaitHelpers;
using System;
using System.Net;
using File = System.IO.File;
namespace MoodleExtraction.Controllers;

[ApiController]
[Route("[controller]")]
public class MoodleScraperController : ControllerBase
{
    [HttpGet("scrape-category")]
    public async Task<IActionResult> ScrapeCategory(int categoryId)
    {
        try
        {
            var options = new ChromeOptions();
            //options.AddArgument("--headless"); // Run in headless mode (no GUI)

            using (var driver = new ChromeDriver(options))
            {
                // Step 1: Login
                driver.Navigate().GoToUrl("https://m3.inpt.ac.ma/login/index.php");
                driver.FindElement(By.Id("username")).SendKeys("alexsys");
                driver.FindElement(By.Id("password")).SendKeys("Alexsys@24");
                driver.FindElement(By.Id("loginbtn")).Click();
                await Task.Delay(2000); // Wait for login to complete

                // Step 2: Navigate to the category page
                string categoryUrl = $"https://m3.inpt.ac.ma/course/index.php?categoryid={categoryId}";
                driver.Navigate().GoToUrl(categoryUrl);

                // Step 3: Extract course names and URLs
                var courseElements = driver.FindElements(By.CssSelector("div.courses-container-inner h4.title a"));
                var courses = courseElements.Select(courseElement => new
                {
                    Name = courseElement.Text,
                    Url = courseElement.GetAttribute("href")
                }).ToList();

                foreach (var course in courses)
                {
                    try
                    {
                        string courseName = course.Name;
                        string courseUrl = course.Url;
                        string sanitizedCourseName = SanitizeFileName(courseName);

                        // Create the directory structure
                        string courseDirectory = Path.Combine("1ere APIC", "SVT", sanitizedCourseName);
                        Directory.CreateDirectory(courseDirectory);

                        // Step 4: Navigate to the course page and extract relevant content
                        await ScrapeCourse(driver, courseUrl, courseDirectory);
                    }
                    catch (StaleElementReferenceException ex)
                    {
                        Console.WriteLine($"Stale element reference for course: {ex.Message}");
                        // Optionally retry or log the error
                        continue;
                    }
                }
            }

            return Ok("Scraping completed successfully.");
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error: {ex.Message}");
        }
    }

    private async Task ScrapeCourse(IWebDriver driver, string courseUrl, string courseDirectory)
    {
        driver.Navigate().GoToUrl(courseUrl);
        await Task.Delay(2000); // Wait for the page to load

        // Step 4a: Extract sections
        var sections = driver.FindElements(By.CssSelector("div[role='main'] ul.tiles li.tile.tile-clickable.phototile.altstyle"));

        int sectionId = 1; // Start with the first section ID

        foreach (var section in sections)
        {
            try
            {
                // Dynamically construct the ID for each section's li element
                string tileId = $"tile-{sectionId}";
                string sectionById = $"section-{sectionId}";

                // Locate the li element with the specific ID and class
                var tileLi = driver.FindElement(By.Id(tileId));

                // Navigate to the div with class "photo-tile-text" within the tileLi
                var photoTileTextDiv = tileLi.FindElement(By.CssSelector("div.photo-tile-text"));

                // Find the h3 element inside the "photo-tile-text" div
                var sectionNameElement = photoTileTextDiv.FindElement(By.TagName("h3"));
                string sectionName = SanitizeFileName(sectionNameElement.Text);

                // Create the directory based on the section name
                string sectionDirectory = Path.Combine(courseDirectory, sectionName);
                Directory.CreateDirectory(sectionDirectory);

                // Find the link with the class "tile-link" within the tileLi
                var activityLinkElement = tileLi.FindElement(By.CssSelector("a.tile-link"));
                string sectionActivityUrl = activityLinkElement.GetAttribute("href");

                // Process the activity
                await ScrapeActivity(driver, sectionActivityUrl, sectionById, sectionDirectory);

                // Return to the main course page after scraping
                driver.Navigate().GoToUrl(courseUrl);
                await Task.Delay(2000); // Wait for the page to load again

                // Increment sectionId for the next section
                sectionId++;
            }
            catch (NoSuchElementException ex)
            {
                Console.WriteLine($"Element not found for section {sectionId}: {ex.Message}");
                // Continue to the next section
                continue;
            }
        }
    }

    private async Task ScrapeActivity(IWebDriver driver, string activityUrl, string sectionId, string sectionDirectory)
    {
        driver.Navigate().GoToUrl(activityUrl);

        // Wait for the section to be visible
        WebDriverWait wait = new WebDriverWait(driver, TimeSpan.FromSeconds(10));
        wait.Until(ExpectedConditions.ElementIsVisible(By.CssSelector("div.format_tiles_section_content")));

        await Task.Delay(2000); // Wait for any additional loading

        // Try to locate elements, handling stale element reference exceptions
        List<IWebElement> sectionElements = new List<IWebElement>();
        bool elementsFound = false;
        int attempts = 0;

        while (!elementsFound && attempts < 3)
        {
            try
            {
                var section = driver.FindElement(By.Id(sectionId));
                var sec = section.FindElement(By.CssSelector("div.format_tiles_section_content"));
                // Find the first <ul> element within the located div
                var ulElement = sec.FindElement(By.CssSelector("ul.section.img-text.nosubtiles"));


                // Get the class name of the <ul> element
                string ulClassName = ulElement.GetAttribute("class");

                // Get the list of <li> elements inside the <ul>
                sectionElements = ulElement.FindElements(By.TagName("li")).ToList();
                elementsFound = true;

            }
            catch (StaleElementReferenceException)
            {
                attempts++;
                await Task.Delay(1000); // Wait and retry
            }
        }

        if (sectionElements.Count == 0)
        {
            // No li elements found, skip to the next section
            return;
        }

        foreach (var section in sectionElements)
        {
            try
            {
                // Check if the section contains a TEST, H5P, or GEOGEBRA
                var typeElement = section.FindElement(By.CssSelector("div.text-uppercase.small"));
                var nameElement = section.FindElement(By.CssSelector("div.activityname a"));
                string type = typeElement.Text.Trim();
                string activityName = SanitizeFileName(nameElement.Text.Trim());
                string sectionActivityUrl = nameElement.GetAttribute("href");

                if (type == "TEST")
                {
                    await DownloadTestContent(driver, sectionActivityUrl, sectionDirectory, activityName);
                }
                else if (type == "H5P")
                {
                    await DownloadH5PContent(driver, sectionActivityUrl, sectionDirectory, activityName);
                }
                else if (type == "GEOGEBRA")
                {
                    await DownloadGeoGebraContent(driver, sectionActivityUrl, sectionDirectory, activityName);
                }
                else if (type == "FICHIER")
                {
                    // Locate the tiles-activity-container to get the data-url
                    var activityContainerElement = section.FindElement(By.CssSelector("div.tiles-activity-container"));
                    string dataUrl = activityContainerElement.GetAttribute("data-url");

                    if (!string.IsNullOrEmpty(dataUrl))
                    {
                        // Download the PDF file
                        await DownloadPdfFile(dataUrl, sectionDirectory);
                    }
                }
            }
            catch (NoSuchElementException)
            {
                // Skip to the next section if any element is not found
                continue;
            }
            catch (StaleElementReferenceException)
            {
                // Handle the stale element exception by retrying
                continue;
            }
        }
    }
    private async Task DownloadTestContent(IWebDriver driver, string testUrl, string sectionDirectory, string activityName)
    {
        driver.Navigate().GoToUrl(testUrl);
        await Task.Delay(2000); // Wait for the page to load

        try
        {
            var continueButton = driver.FindElement(By.CssSelector("button.btn.btn-primary"));
            continueButton.Click();
            await Task.Delay(2000); // Wait for the test page to load

            string testPageHtml = driver.PageSource;

            // Remove unwanted HTML elements
            testPageHtml = RemoveUnwantedHtmlElements(testPageHtml);
            // Create directories for scripts, styles, and images
            string scriptsDirectory = Path.Combine(sectionDirectory, "scripts");
            string stylesDirectory = Path.Combine(sectionDirectory, "styles");
            string imagesDirectory = Path.Combine(sectionDirectory, "images");

            Directory.CreateDirectory(scriptsDirectory);
            Directory.CreateDirectory(stylesDirectory);
            Directory.CreateDirectory(imagesDirectory);

            // Download and replace external resources
            testPageHtml = await DownloadAndReplaceResources(driver, testPageHtml, scriptsDirectory, stylesDirectory, imagesDirectory);

            // Save the modified HTML content to a file
            string testFilePath = Path.Combine(sectionDirectory, $"{activityName}.html");
            await System.IO.File.WriteAllTextAsync(testFilePath, testPageHtml);
        }
        catch (NoSuchElementException)
        {
            // Handle the case where the continue button is not found
        }
    }
    private string RemoveUnwantedHtmlElements(string htmlContent)
    {
        // Load the HTML document
        HtmlDocument document = new HtmlDocument();
        document.LoadHtml(htmlContent);

        // Define a list of XPath expressions for elements to be removed
        string[] xpathsToRemove = {
        "//div[@id='fsmod-header']",
        "//div[@class='container-fluid tertiary-navigation']",
        "//div[@class='theme-coursenav flexcols onlynext']",
        "//div[@id='course-panel']",
        "//div[@id='fsmod-sidebar']"
    };

        // Remove the elements
        foreach (string xpath in xpathsToRemove)
        {
            var nodesToRemove = document.DocumentNode.SelectNodes(xpath);
            if (nodesToRemove != null)
            {
                foreach (var node in nodesToRemove)
                {
                    node.Remove();
                }
            }
        }

        // Return the modified HTML content
        return document.DocumentNode.OuterHtml;
    }
    private async Task<string> DownloadAndReplaceResources(IWebDriver driver, string htmlContent, string scriptsDirectory, string stylesDirectory, string imagesDirectory)
    {
        // Patterns to match script, link, img, favicon, and any URL starting with https://m3.inpt.ac.ma/
        string scriptPattern = @"<script.*?src=[""'](.*?)[""'].*?></script>";
        string cssPattern = @"<link.*?href=[""'](.*?)[""'].*?/>";
        string imgPattern = @"<img.*?src=[""'](.*?)[""'].*?>";
        string faviconPattern = @"<link.*?rel=[""']shortcut icon[""'].*?href=[""'](.*?)[""'].*?/>";
        string generalPattern = @"https://m3.inpt.ac.ma/.*?[""'\s>]"; // Matches any URL starting with https://m3.inpt.ac.ma/

        // Download and replace scripts
        htmlContent = await DownloadAndReplace(driver, htmlContent, scriptPattern, "src", scriptsDirectory);

        // Download and replace CSS files
        htmlContent = await DownloadAndReplace(driver, htmlContent, cssPattern, "href", stylesDirectory);

        // Download and replace images
        htmlContent = await DownloadAndReplaceImages(driver, htmlContent, imagesDirectory);

        // Download and replace favicon
        htmlContent = await DownloadAndReplace(driver, htmlContent, faviconPattern, "href", imagesDirectory);

        // Download and replace any general resources starting with https://m3.inpt.ac.ma/
        htmlContent = await DownloadAndReplace(driver, htmlContent, generalPattern, null, imagesDirectory);

        return htmlContent;
    }
    private async Task<string> DownloadAndReplaceImages(IWebDriver driver, string htmlContent, string imagesDirectory)
    {
        var matches = Regex.Matches(htmlContent, @"<img.*?src=[""'](.*?)[""'].*?>", RegexOptions.IgnoreCase);

        foreach (Match match in matches)
        {
            string url = match.Groups[1].Value;
            if (!url.StartsWith("http"))
            {
                // Handle relative URLs by making them absolute
                url = "https://m3.inpt.ac.ma" + url;
            }

            try
            {
                // Open the image URL in a new tab
                ((IJavaScriptExecutor)driver).ExecuteScript("window.open();");
                driver.SwitchTo().Window(driver.WindowHandles.Last());
                driver.Navigate().GoToUrl(url);

                // Wait for the image to load
                await Task.Delay(2000);

                // Get the file name and save path
                Uri uri = new Uri(url);
                string fileName = Path.GetFileName(uri.LocalPath);
                string filePath = Path.Combine(imagesDirectory, fileName);

                // Trigger download using JavaScript
                ((IJavaScriptExecutor)driver).ExecuteScript($@"
                var link = document.createElement('a');
                link.href = '{url}';
                link.download = '{fileName}';
                document.body.appendChild(link);
                link.click();
                document.body.removeChild(link);");

                // Replace the URL in the HTML with the relative path
                string relativePath = Path.Combine(Path.GetFileName(imagesDirectory), fileName).Replace("\\", "/");
                htmlContent = htmlContent.Replace(url, relativePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to download image: {url}. Error: {ex.Message}");
            }
            finally
            {
                // Close the current tab and switch back to the original tab
                driver.Close();
                driver.SwitchTo().Window(driver.WindowHandles.First());
            }
        }

        return htmlContent;
    }

    private async Task<string> DownloadAndReplace(IWebDriver driver, string htmlContent, string pattern, string attribute, string directory)
    {
        var matches = Regex.Matches(htmlContent, pattern, RegexOptions.IgnoreCase);

        foreach (Match match in matches)
        {
            string url;
            if (attribute != null)
            {
                url = match.Groups[1].Value;
            }
            else
            {
                // For the general pattern, extract the full match
                url = match.Value.TrimEnd('"', '\'', ' ', '>');
            }

            if (!url.StartsWith("http") && !url.StartsWith("//") && !url.StartsWith("https://m3.inpt.ac.ma/"))
            {
                // Skip if the URL is not an absolute, protocol-relative URL, or doesn't start with the specified base URL
                continue;
            }

            // Handle protocol-relative URLs
            if (url.StartsWith("//"))
            {
                url = "https:" + url;
            }

            try
            {
                // Open the resource in a new tab
                ((IJavaScriptExecutor)driver).ExecuteScript("window.open();");
                driver.SwitchTo().Window(driver.WindowHandles.Last());
                driver.Navigate().GoToUrl(url);

                // Wait for the page/resource to load
                await Task.Delay(2000);

                // Get the file name and save path
                Uri uri = new Uri(url);
                string fileName = Path.GetFileName(uri.LocalPath);
                string filePath = Path.Combine(directory, fileName);

                // Download the resource using HttpClient
                using (HttpClient client = new HttpClient())
                {
                    var resourceBytes = await client.GetByteArrayAsync(url);
                    await System.IO.File.WriteAllBytesAsync(filePath, resourceBytes);
                }

                // Replace the URL in the HTML with the relative path
                string relativePath = Path.Combine(Path.GetFileName(directory), fileName).Replace("\\", "/");
                htmlContent = htmlContent.Replace(url, relativePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to download resource: {url}. Error: {ex.Message}");
            }
            finally
            {
                // Close the current tab and switch back to the original tab
                driver.Close();
                driver.SwitchTo().Window(driver.WindowHandles.First());
            }
        }

        return htmlContent;
    }


    private async Task DownloadH5PContent(IWebDriver driver, string h5pUrl, string sectionDirectory, string activityName)
    {
        // Open a new tab and switch to it
        ((IJavaScriptExecutor)driver).ExecuteScript("window.open();");
        driver.SwitchTo().Window(driver.WindowHandles.Last());

        // Navigate to the H5P content URL in the new tab
        driver.Navigate().GoToUrl(h5pUrl);
        await Task.Delay(2000); // Wait for the page to load

        var exportUrls = new List<string>();

        // Get all <script> elements
        var scriptElements = driver.FindElements(By.TagName("script"));

        foreach (var scriptElement in scriptElements)
        {
            var scriptText = scriptElement.GetAttribute("innerHTML");
            if (scriptText.Contains("var H5PIntegration = "))
            {
                string startPattern = "\r\n//<![CDATA[\r\nvar H5PIntegration = ";
                string endPattern = ";\r\n//]]>";

                int startIndex = scriptText.IndexOf(startPattern) + startPattern.Length;
                int endIndex = scriptText.IndexOf(endPattern, startIndex);
                string jsonSubstring = scriptText.Substring(startIndex, endIndex - startIndex).Trim();

                // Deserialize JSON substring into JsonNode
                JsonNode jsonObject = JsonNode.Parse(jsonSubstring);
                FindExportUrls(jsonObject["contents"], exportUrls);

                string exportUrl = exportUrls[0];

                if (!string.IsNullOrEmpty(exportUrl))
                {
                    // Download the file from the export URL using the authenticated session
                    string h5pFilePath = await DownloadFileFromUrlWithCookies(driver, exportUrl, sectionDirectory);

                    // Unzip the .h5p file into the section directory
                    if (!string.IsNullOrEmpty(h5pFilePath) && System.IO.File.Exists(h5pFilePath))
                    {
                        UnzipH5PFile(h5pFilePath, sectionDirectory);
                        // Optionally delete the original .h5p file after extraction
                        System.IO.File.Delete(h5pFilePath);
                    }
                }

                break;
            }
        }

        // Close the current tab
        driver.Close();

        // Switch back to the original tab
        driver.SwitchTo().Window(driver.WindowHandles.First());
    }

    private async Task DownloadGeoGebraContent(IWebDriver driver, string geoGebraUrl, string sectionDirectory, string activityName)
    {
        // Open a new tab and switch to it
        ((IJavaScriptExecutor)driver).ExecuteScript("window.open();");
        driver.SwitchTo().Window(driver.WindowHandles.Last());

        // Navigate to the GeoGebra content URL in the new tab
        driver.Navigate().GoToUrl(geoGebraUrl);
        await Task.Delay(2000); // Wait for the page to load

        try
        {
            // Extract the HTML content from the no-overflow div
            var noOverflowDiv = driver.FindElement(By.CssSelector("div.no-overflow"));
            string geoGebraHtml = noOverflowDiv.GetAttribute("outerHTML");

            // Save the HTML content to a file
            string geoGebraFilePath = Path.Combine(sectionDirectory, $"{activityName}_question.html");
            await System.IO.File.WriteAllTextAsync(geoGebraFilePath, geoGebraHtml);

            // Extract the ggbBase64 value from the script elements
            var scriptElements = driver.FindElements(By.TagName("script"));
            foreach (var scriptElement in scriptElements)
            {
                var scriptText = scriptElement.GetAttribute("innerHTML");
                if (scriptText.Contains("ggbBase64"))
                {
                    string base64Pattern = @"ggbBase64:\s*\""([^\""]+)\""";

                    var match = Regex.Match(scriptText, base64Pattern);
                    if (match.Success)
                    {
                        string base64String = match.Groups[1].Value;
                        string base64FilePath = Path.Combine(sectionDirectory, $"{activityName}_base64.txt");
                        await System.IO.File.WriteAllTextAsync(base64FilePath, base64String);
                    }
                    break;
                }
            }
        }
        finally
        {
            driver.Close();
            driver.SwitchTo().Window(driver.WindowHandles.First());
        }
    }
    private async Task DownloadPdfFile(string fileUrl, string directory)
    {
        using (var client = new HttpClient())
        {
            var fileName = Path.GetFileName(fileUrl);
            var filePath = Path.Combine(directory, fileName);

            var pdfBytes = await client.GetByteArrayAsync(fileUrl);
            await System.IO.File.WriteAllBytesAsync(filePath, pdfBytes);

            Console.WriteLine($"PDF downloaded to: {filePath}");
        }
    }

    private string SanitizeFileName(string fileName)
    {
        return string.Join("_", fileName.Split(Path.GetInvalidFileNameChars()));
    }
    private void FindExportUrls(JsonNode node, List<string> exportUrls)
    {
        if (node is JsonObject obj)
        {
            foreach (var kvp in obj)
            {
                if (kvp.Key == "exportUrl" && kvp.Value is JsonValue val)
                {
                    exportUrls.Add(val.ToString());
                }
                else if (kvp.Value is JsonObject || kvp.Value is JsonArray)
                {
                    FindExportUrls(kvp.Value, exportUrls);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                FindExportUrls(item, exportUrls);
            }
        }
    }
    // Method to download a file from a URL
    private async Task<string> DownloadFileFromUrlWithCookies(IWebDriver driver, string fileUrl, string directory)
    {
        using (var handler = new HttpClientHandler())
        {
            // Get cookies from the Selenium WebDriver
            var cookies = driver.Manage().Cookies.AllCookies;
            handler.CookieContainer = new CookieContainer();

            foreach (var cookie in cookies)
            {
                handler.CookieContainer.Add(new System.Net.Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain));
            }

            using (var client = new HttpClient(handler))
            {
                try
                {
                    var fileName = Path.GetFileName(fileUrl);
                    var filePath = Path.Combine(directory, fileName);

                    var fileBytes = await client.GetByteArrayAsync(fileUrl);
                    await System.IO.File.WriteAllBytesAsync(filePath, fileBytes);

                    Console.WriteLine($"File downloaded to: {filePath}");
                    return filePath;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to download file from URL: {fileUrl}. Error: {ex.Message}");
                    return null;
                }
            }
        }
    }
    // Method to unzip the .h5p file
    private void UnzipH5PFile(string h5pFilePath, string destinationDirectory)
    {
        try
        {
            // Get the file name without extension to create a folder
            string folderName = Path.GetFileNameWithoutExtension(h5pFilePath);
            string extractionPath = Path.Combine(destinationDirectory, folderName);

            // Create the directory
            Directory.CreateDirectory(extractionPath);

            // Extract the contents of the .h5p file into the created folder
            ZipFile.ExtractToDirectory(h5pFilePath, extractionPath);
            Console.WriteLine($"Extracted .h5p file to: {extractionPath}");

            // Optional: Create an index.html for easy access or viewing
            CreateIndexHtml(extractionPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to unzip .h5p file: {h5pFilePath}. Error: {ex.Message}");
        }
    }
    // Method to create an index.html (optional)
    private void CreateIndexHtml(string directory)
    {
        string indexPath = Path.Combine(directory, "index.html");
        string htmlContent = "<html><body><h1>H5P Content</h1></body></html>";

        System.IO.File.WriteAllText(indexPath, htmlContent);
        Console.WriteLine($"Created index.html at: {indexPath}");
    }
}
