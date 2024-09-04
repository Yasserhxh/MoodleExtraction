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
using Azure.Storage.Blobs;
using MoodleExtraction.Models;

namespace MoodleExtraction.Controllers
{
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

                    // Step 3: Extract course names, URLs, and images
                    var courseElements = driver.FindElements(By.CssSelector("div.theme-course-item-inner"));
                    var courses = new List<dynamic>();

                    foreach (var courseElement in courseElements)
                    {
                        try
                        {
                            var nameElement = courseElement.FindElement(By.CssSelector("h4.title a"));
                            var imageElement = courseElement.FindElement(By.CssSelector("div.image-wrap div.image img"));

                            string courseName = nameElement.Text;
                            string courseUrl = nameElement.GetAttribute("href");
                            string imageUrl = imageElement.GetAttribute("src");

                            string sanitizedCourseName = SanitizeFileName(courseName);
                            string courseDirectory = Path.Combine("1ere APIC", "SVT", sanitizedCourseName);
                            Directory.CreateDirectory(courseDirectory);

                            // **Download the course image immediately after creating the folder**
                            await DownloadImage(driver, imageUrl, courseDirectory);

                            courses.Add(new { Name = courseName, Url = courseUrl });
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error processing course: {ex.Message}");
                            continue;
                        }
                    }

                    foreach (var course in courses)
                    {
                        try
                        {
                            // Step 4: Navigate to the course page and extract relevant content
                            string courseDirectory = Path.Combine("1ere APIC", "SVT", SanitizeFileName(course.Name));

                            await ScrapeCourse(driver, course.Url, Path.Combine("1ere APIC", "SVT", SanitizeFileName(course.Name)));
                            // Assume elements are downloaded and stored in a list called downloadedElements
                            List<ElementProgramme> elementProgrammes =
                            [
                                new ElementProgramme { Level = 1, Code = "2d1d190d-5150-42f3-879d-0097063c806e" },
                                new ElementProgramme { Level = 2, Code = "395c7c8e-475a-45e9-a0b4-11ef241a6463" },
                                new ElementProgramme { Level = 3, Code = "d4892214-c5a8-4294-9eda-c3c552d49b79" }
                            ];

                            await GenerateCourseJson(course.Name, courseDirectory, course.Image, courses, elementProgrammes);
                        }
                        catch (StaleElementReferenceException ex)
                        {
                            Console.WriteLine($"Stale element reference for course: {ex.Message}");
                            continue;
                        }
                    }
                }
                await UploadToBlobStorageAsync(); // Upload after scraping

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

            // Step 4a: Extract sections and images within the content
            var sections = driver.FindElements(By.CssSelector("div[role='main'] ul.tiles li.tile.tile-clickable.phototile.altstyle"));

            int sectionId = 1;

            foreach (var section in sections)
            {
                try
                {
                    string tileId = $"tile-{sectionId}";
                    string sectionById = $"section-{sectionId}";

                    var tileLi = driver.FindElement(By.Id(tileId));
                    var photoTileTextDiv = tileLi.FindElement(By.CssSelector("div.photo-tile-text"));

                    var sectionNameElement = photoTileTextDiv.FindElement(By.TagName("h3"));
                    string sectionName = SanitizeFileName(sectionNameElement.Text);
                    string sectionDirectory = Path.Combine(courseDirectory, sectionName);
                    Directory.CreateDirectory(sectionDirectory);

                    var activityLinkElement = tileLi.FindElement(By.CssSelector("a.tile-link"));
                    string sectionActivityUrl = activityLinkElement.GetAttribute("href");

                    // Scrape activity and images in course content
                    await ScrapeActivity(driver, sectionActivityUrl, sectionById, sectionDirectory, sectionName);

                    driver.Navigate().GoToUrl(courseUrl);
                    await Task.Delay(2000);

                    sectionId++;
                }
                catch (NoSuchElementException ex)
                {
                    Console.WriteLine($"Element not found for section {sectionId}: {ex.Message}");
                    continue;
                }
            }
        }

        private async Task ScrapeActivity(IWebDriver driver, string activityUrl, string sectionId, string sectionDirectory, string sectionName)
        {
            driver.Navigate().GoToUrl(activityUrl);

            // Wait for the section to be visible
            WebDriverWait wait = new WebDriverWait(driver, TimeSpan.FromSeconds(10));
            wait.Until(ExpectedConditions.ElementIsVisible(By.CssSelector("div.format_tiles_section_content")));

            await Task.Delay(2000); // Wait for any additional loading

            // Find all images inside the content div
          /*  var contentDiv = driver.FindElement(By.CssSelector("div.content"));
            var imgElements = contentDiv.FindElements(By.CssSelector("img"));

            foreach (var imgElement in imgElements)
            {
                string imgSrc = imgElement.GetAttribute("src");
                await DownloadImage(driver, imgSrc, sectionDirectory);
            }*/

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
                            await DownloadPdfFile(driver, dataUrl, sectionDirectory);
                        }
                    }
                    else if (type == "PAGE")
                    {
                        await DownloadPageContent(driver, sectionActivityUrl, sectionDirectory, activityName);
                    }
                    else if (type == "MEDIA")
                    {
                        // Locate the video tag within the section
                        var videoElement = section.FindElement(By.CssSelector("video"));

                        if (videoElement != null)
                        {
                            string videoUrl = videoElement.GetAttribute("src");

                            if (!string.IsNullOrEmpty(videoUrl))
                            {
                                // Download the video file
                                await DownloadVideoFile(driver, videoUrl, sectionDirectory);
                            }
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
        private async Task DownloadVideoFile(IWebDriver driver, string videoUrl, string directory)
        {
            try
            {
                // Open the video URL in a new tab and switch to it
                ((IJavaScriptExecutor)driver).ExecuteScript("window.open();");
                driver.SwitchTo().Window(driver.WindowHandles.Last());
                driver.Navigate().GoToUrl(videoUrl);

                await Task.Delay(2000); // Wait for the video to load

                Uri uri = new Uri(videoUrl);
                string fileName = Path.GetFileName(uri.LocalPath);

                // If the file has no extension, assume it's a video and add ".mp4"
                if (string.IsNullOrEmpty(Path.GetExtension(fileName)))
                {
                    fileName += ".mp4";
                }

                string filePath = Path.Combine(directory, fileName);

                // Use HttpClient with cookie handling to download the video
                using (var handler = new HttpClientHandler())
                {
                    var cookies = driver.Manage().Cookies.AllCookies;
                    handler.CookieContainer = new CookieContainer();

                    foreach (var cookie in cookies)
                    {
                        handler.CookieContainer.Add(new System.Net.Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain));
                    }

                    using (HttpClient client = new HttpClient(handler))
                    {
                        var videoBytes = await client.GetByteArrayAsync(videoUrl);
                        await System.IO.File.WriteAllBytesAsync(filePath, videoBytes);
                        Console.WriteLine($"Video downloaded to: {filePath}");
                    }
                }

                // Close the new tab and switch back to the original tab
                driver.Close();
                driver.SwitchTo().Window(driver.WindowHandles.First());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to download video: {videoUrl}. Error: {ex.Message}");
            }
        }

        private async Task DownloadImage(IWebDriver driver, string imageUrl, string directory)
        {
            try
            {
                // Open image in a new tab and download
                ((IJavaScriptExecutor)driver).ExecuteScript("window.open();");
                driver.SwitchTo().Window(driver.WindowHandles.Last());

                driver.Navigate().GoToUrl(imageUrl);
                await Task.Delay(2000); // Wait for the image to load

                Uri uri = new Uri(imageUrl);
                string fileName = Path.GetFileName(uri.LocalPath);

                // Check if the file has no extension and is likely an image
                if (string.IsNullOrEmpty(Path.GetExtension(fileName)))
                {
                    fileName += ".jpg"; // Add .jpg extension if missing
                }

                string filePath = Path.Combine(directory, fileName);

                using (var handler = new HttpClientHandler())
                {
                    var cookies = driver.Manage().Cookies.AllCookies;
                    handler.CookieContainer = new CookieContainer();

                    foreach (var cookie in cookies)
                    {
                        handler.CookieContainer.Add(new System.Net.Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain));
                    }

                    using (HttpClient client = new HttpClient(handler))
                    {
                        var imageBytes = await client.GetByteArrayAsync(imageUrl);
                        await System.IO.File.WriteAllBytesAsync(filePath, imageBytes);
                    }
                }

                driver.Close();
                driver.SwitchTo().Window(driver.WindowHandles.First());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to download image: {imageUrl}. Error: {ex.Message}");
            }
        }

        private async Task DownloadPageContent(IWebDriver driver, string pageUrl, string sectionDirectory, string activityName)
        {
            // Open a new tab and switch to it
            ((IJavaScriptExecutor)driver).ExecuteScript("window.open();");
            driver.SwitchTo().Window(driver.WindowHandles.Last());

            // Navigate to the PAGE URL in the new tab
            driver.Navigate().GoToUrl(pageUrl);
            await Task.Delay(2000); // Wait for the page to load

            // Step 1: Extract JS and CSS files
            string pageHtml = driver.PageSource;

            // Remove unwanted HTML elements
            pageHtml = RemoveUnwantedHtmlElements(pageHtml);
            string scriptsDirectory = Path.Combine(sectionDirectory, "scripts");
            string stylesDirectory = Path.Combine(sectionDirectory, "styles");
            string imagesDirectory = Path.Combine(sectionDirectory, "images");

            Directory.CreateDirectory(scriptsDirectory);
            Directory.CreateDirectory(stylesDirectory);
            Directory.CreateDirectory(imagesDirectory);

            pageHtml = await DownloadAndReplaceResources(driver, pageHtml, scriptsDirectory, stylesDirectory, imagesDirectory);

            // Step 2: Extract HTML content from the main div
            var mainDiv = driver.FindElement(By.CssSelector("div[role='main']"));
            string mainContentHtml = mainDiv.GetAttribute("outerHTML");

            // Add meta charset tag and combine the modified HTML content with the main content
            pageHtml = "<html><head><meta charset=\"UTF-8\">" + pageHtml + "</head><body>" + mainContentHtml + "</body></html>";

            // Step 3: Download media files
            pageHtml = await DownloadImagesAndUpdatePaths(driver, pageHtml, imagesDirectory);
            pageHtml = await DownloadVideosAndUpdatePaths(driver, pageHtml, imagesDirectory);

            // Save the final HTML content to a file
            string pageFilePath = Path.Combine(sectionDirectory, $"{activityName}.html");
            await System.IO.File.WriteAllTextAsync(pageFilePath, pageHtml);

            // Close the new tab and switch back to the original tab
            driver.Close();
            driver.SwitchTo().Window(driver.WindowHandles.First());
        }

        private async Task<string> DownloadAndReplaceResources(IWebDriver driver, string htmlContent, string scriptsDirectory, string stylesDirectory, string imagesDirectory)
        {
            // Patterns to match script, link, img, favicon, and any URL starting with https://m3.inpt.ac.ma/
            string scriptPattern = @"<script.*?src=[""'](.*?)[""'].*?></script>";
            string cssPattern = @"<link[^>]*href=['""](?<url>.*?)['""](?:[^>]*charset=['""]utf-8['""])?[^>]*>";
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
            //htmlContent = await DownloadAndReplace(driver, htmlContent, generalPattern, null, imagesDirectory);

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

                    // Get the file name
                    Uri uri = new Uri(url);
                    string fileName = Path.GetFileName(uri.LocalPath);
                    string downloadsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

                    string tempFilePath = Path.Combine(downloadsDirectory, fileName);

                    // Trigger download using JavaScript
                    ((IJavaScriptExecutor)driver).ExecuteScript($@"
                var link = document.createElement('a');
                link.href = '{url}';
                link.download = '{fileName}';
                document.body.appendChild(link);
                link.click();
                document.body.removeChild(link);");

                    // Wait for the file to appear in the Downloads folder
                    await WaitForFileToDownload(tempFilePath);

                    // Move the file to the desired directory
                    string finalFilePath = Path.Combine(imagesDirectory, fileName);
                    System.IO.File.Move(tempFilePath, finalFilePath);

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
        private async Task WaitForFileToDownload(string filePath)
        {
            int attempts = 0;
            while (!System.IO.File.Exists(filePath) && attempts < 30)
            {
                await Task.Delay(1000); // Wait for 1 second
                attempts++;
            }

            if (attempts >= 30)
            {
                throw new TimeoutException("File download timed out.");
            }
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

                    // Check if the file has no extension and is likely a CSS file
                    if (string.IsNullOrEmpty(Path.GetExtension(fileName)))
                    {
                        fileName += ".css"; // Add .css extension
                    }

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

        private async Task<string> DownloadImagesAndUpdatePaths(IWebDriver driver, string htmlContent, string sectionDirectory)
        {
            var matches = Regex.Matches(htmlContent, @"<img.*?src=[""'](.*?)[""'].*?>", RegexOptions.IgnoreCase);

            foreach (Match match in matches)
            {
                string imageUrl = match.Groups[1].Value;

                if (!imageUrl.StartsWith("http"))
                {
                    imageUrl = "https://m3.inpt.ac.ma" + imageUrl;
                }

                try
                {
                    // Get the file name from the URL
                    Uri uri = new Uri(imageUrl);
                    string fileName = Path.GetFileName(uri.LocalPath);

                    // Check if the file has no extension and is likely a CSS file
                    if (string.IsNullOrEmpty(Path.GetExtension(fileName)))
                    {
                        fileName += ".css"; // Add .css extension
                    }

                    string filePath = Path.Combine(sectionDirectory, fileName);

                    // Open a new tab
                    ((IJavaScriptExecutor)driver).ExecuteScript("window.open();");
                    driver.SwitchTo().Window(driver.WindowHandles.Last());

                    // Navigate to the image URL
                    driver.Navigate().GoToUrl(imageUrl);

                    // Wait for the image to load
                    await Task.Delay(2000);

                    // Use HttpClient with cookie handling to download the image
                    using (var handler = new HttpClientHandler())
                    {
                        var cookies = driver.Manage().Cookies.AllCookies;
                        handler.CookieContainer = new CookieContainer();

                        foreach (var cookie in cookies)
                        {
                            handler.CookieContainer.Add(new System.Net.Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain));
                        }

                        using (HttpClient client = new HttpClient(handler))
                        {
                            var imageBytes = await client.GetByteArrayAsync(imageUrl);
                            await System.IO.File.WriteAllBytesAsync(filePath, imageBytes);
                        }
                    }

                    // Close the new tab and switch back to the original tab
                    driver.Close();
                    driver.SwitchTo().Window(driver.WindowHandles.First());

                    // Replace the URL in the HTML with the relative path
                    string relativePath = fileName; // Since the image will be in the same directory
                    htmlContent = htmlContent.Replace(imageUrl, relativePath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to download image: {imageUrl}. Error: {ex.Message}");
                }
            }

            return htmlContent;
        }

        private async Task<string> DownloadVideosAndUpdatePaths(IWebDriver driver, string htmlContent, string sectionDirectory)
        {
            var matches = Regex.Matches(htmlContent, @"<video.*?src=[""'](.*?)[""'].*?>", RegexOptions.IgnoreCase);

            foreach (Match match in matches)
            {
                string videoUrl = match.Groups[1].Value;

                if (!videoUrl.StartsWith("http"))
                {
                    videoUrl = "https://m3.inpt.ac.ma" + videoUrl;
                }

                try
                {
                    // Get the file name from the URL
                    Uri uri = new Uri(videoUrl);
                    string fileName = Path.GetFileName(uri.LocalPath);
                    string filePath = Path.Combine(sectionDirectory, fileName);

                    // Open a new tab
                    ((IJavaScriptExecutor)driver).ExecuteScript("window.open();");
                    driver.SwitchTo().Window(driver.WindowHandles.Last());

                    // Navigate to the video URL
                    driver.Navigate().GoToUrl(videoUrl);

                    // Wait for the video to load
                    await Task.Delay(2000);

                    // Use HttpClient with cookie handling to download the video
                    using (var handler = new HttpClientHandler())
                    {
                        var cookies = driver.Manage().Cookies.AllCookies;
                        handler.CookieContainer = new CookieContainer();

                        foreach (var cookie in cookies)
                        {
                            handler.CookieContainer.Add(new System.Net.Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain));
                        }

                        using (HttpClient client = new HttpClient(handler))
                        {
                            var videoBytes = await client.GetByteArrayAsync(videoUrl);
                            await System.IO.File.WriteAllBytesAsync(filePath, videoBytes);
                        }
                    }

                    // Close the new tab and switch back to the original tab
                    driver.Close();
                    driver.SwitchTo().Window(driver.WindowHandles.First());

                    // Replace the URL in the HTML with the relative path
                    string relativePath = fileName; // Since the video will be in the same directory
                    htmlContent = htmlContent.Replace(videoUrl, relativePath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to download video: {videoUrl}. Error: {ex.Message}");
                }
            }

            return htmlContent;
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

            // Define a list of XPath expressions for elements to be hidden
            string[] xpathsToHide = {
                "//div[@id='fsmod-header']",
                "//div[@class='container-fluid tertiary-navigation']",
                "//div[@class='theme-coursenav flexcols onlynext']",
                "//div[@id='course-panel']",
                "//div[@id='fsmod-sidebar']"
            };

            // Hide the elements by adding a style attribute with 'display: none;'
            foreach (string xpath in xpathsToHide)
            {
                var nodesToHide = document.DocumentNode.SelectNodes(xpath);
                if (nodesToHide != null)
                {
                    foreach (var node in nodesToHide)
                    {
                        // Check if the node already has a 'style' attribute
                        var styleAttribute = node.Attributes["style"];
                        if (styleAttribute == null)
                        {
                            // If not, add a new 'style' attribute
                            node.Attributes.Add("style", "display: none;");
                        }
                        else
                        {
                            // If 'style' attribute exists, append 'display: none;' to it
                            styleAttribute.Value += " display: none;";
                        }
                    }
                }
            }

            // Return the modified HTML content
            return document.DocumentNode.OuterHtml;
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
                        // Define the new H5P folder structure
                        string h5pRootDirectory = Path.Combine("H5P", SanitizeFileName(activityName));
                        Directory.CreateDirectory(h5pRootDirectory);

                        // Download the file from the export URL using the authenticated session
                        string h5pFilePath = await DownloadFileFromUrlWithCookies(driver, exportUrl, h5pRootDirectory);

                        // Unzip the .h5p file into the H5P directory
                        if (!string.IsNullOrEmpty(h5pFilePath) && System.IO.File.Exists(h5pFilePath))
                        {
                            UnzipH5PFile(h5pFilePath, h5pRootDirectory);
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

        private async Task DownloadPdfFile(IWebDriver driver, string fileUrl, string directory)
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

                    var fileName = Path.GetFileName(fileUrl);
                    var filePath = Path.Combine(directory, fileName);

                    var pdfBytes = await client.GetByteArrayAsync(fileUrl);
                    await System.IO.File.WriteAllBytesAsync(filePath, pdfBytes);

                    Console.WriteLine($"PDF downloaded to: {filePath}");
                }
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

                        // Check if the file has no extension and is likely a CSS file
                        if (string.IsNullOrEmpty(Path.GetExtension(fileName)))
                        {
                            fileName += ".css"; // Add .css extension
                        }

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

        private void CreateIndexHtml(string directory)
        {
            string indexPath = Path.Combine(directory, "index.html");
            string htmlContent = "<html><body><h1>H5P Content</h1></body></html>";

            System.IO.File.WriteAllText(indexPath, htmlContent);
            Console.WriteLine($"Created index.html at: {indexPath}");
        }
        private async Task UploadToBlobStorageAsync()
        {
            string connectionString = "DefaultEndpointsProtocol=https;AccountName=stppncours;AccountKey=mqsGyKxuR2tnJ5RpqRjR/wTgKlrVCFo4mV2H3vAlTuvmoYY5jRMxQ/UkQqCGLO5yOXwTXLYEcm+p+AStwCbyjQ==;EndpointSuffix=core.windows.net";

            BlobServiceClient blobServiceClient = new BlobServiceClient(connectionString);

            // Upload 1ere APIC to the 'courses' container
            string coursesContainerName = "courses";
            BlobContainerClient coursesContainerClient = blobServiceClient.GetBlobContainerClient(coursesContainerName);
            await coursesContainerClient.CreateIfNotExistsAsync();

            string coursesDirectoryPath = "1ere APIC";
            await UploadDirectoryToBlobContainer(coursesContainerClient, coursesDirectoryPath, coursesDirectoryPath);

            // Upload H5P to the 'h5p' container
            string h5pContainerName = "h5p";
            BlobContainerClient h5pContainerClient = blobServiceClient.GetBlobContainerClient(h5pContainerName);
            await h5pContainerClient.CreateIfNotExistsAsync();

            string h5pDirectoryPath = "H5P";
            await UploadDirectoryToBlobContainer(h5pContainerClient, h5pDirectoryPath, h5pDirectoryPath);
        }

        private async Task UploadDirectoryToBlobContainer(BlobContainerClient containerClient, string directoryPath, string baseDirectoryPath)
        {
            foreach (var filePath in Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                string blobName = filePath.Substring(baseDirectoryPath.Length + 1).Replace("\\", "/");
                BlobClient blobClient = containerClient.GetBlobClient(blobName);

                using FileStream fileStream = System.IO.File.OpenRead(filePath);
                await blobClient.UploadAsync(fileStream, overwrite: true);
                Console.WriteLine($"Uploaded {blobName} to {containerClient.Name} container.");
            }
        }
        private async Task GenerateCourseJson(string courseName, string courseDirectory, string courseImagePath, List<dynamic> elements, List<ElementProgramme> elementProgrammes)
        {
            var courseId = Guid.NewGuid().ToString();
            var courseJson = new CourseJson
            {
                CourseName = courseName,
                CourseId = courseId,
                Photo = courseImagePath.Replace("\\", "/"),
                ProfessorId = "dfe910f4-c065-46cd-b23c-265ddc85a8ed", // Static for now
                CodeNiveau = "2A32101010", // Static for now
                CodeClasse = "9210d27e-ec40-4568-9fbe-40146f9f1c2f", // Static for now
                ElementProgrammes = elementProgrammes,
                Elements = new List<Element>()
            };

            int elementId = 1;
            foreach (var element in elements)
            {
                string elementName = element.Name;
                string elementDirectory = Path.Combine(courseDirectory, SanitizeFileName(elementName));

                var courseElement = new Element
                {
                    ElementName = elementName,
                    ElementId = elementId.ToString(),
                    CourseId = courseId,
                    IconBody = "<i class=\"ki-duotone text-primary ki-book-open fs-3x\"><span class=\"path1\"></span><span class=\"path2\"></span><span class=\"path3\"></span><span class=\"path4\"></span></i>",
                    Link = $"/Courses/{courseName}/{SanitizeFileName(elementName)}",
                    IsDownloaded = true,
                    Contents = new List<Content>()
                };

                // Fetch the contents based on downloaded files and types
                foreach (var filePath in Directory.GetFiles(elementDirectory, "*", SearchOption.AllDirectories))
                {
                    var content = new Content
                    {
                        ContentName = Path.GetFileNameWithoutExtension(filePath),
                        FilesUrls = new List<string> { filePath.Replace("\\", "/").Replace(courseDirectory + "/", "") }
                    };

                    if (filePath.EndsWith(".html"))
                    {
                        content.Type = "html";
                    }
                    else if (filePath.EndsWith(".pdf"))
                    {
                        content.Type = "file";
                    }
                    else if (filePath.EndsWith(".mp4"))
                    {
                        content.Type = "media";
                    }
                    else if (filePath.EndsWith("_question.html"))
                    {
                        content.Type = "geogebra";
                        content.Width = "960";  // Default width
                        content.Height = "560"; // Default height
                        content.Base64 = Path.Combine(elementDirectory, content.ContentName + "_base64.txt").Replace("\\", "/");
                    }
                    else if (Directory.Exists(filePath) && Path.GetFileName(filePath).EndsWith(".h5p"))
                    {
                        content.Type = "H5P";
                        content.FilesUrls = Directory.GetFiles(filePath).Select(f => f.Replace("\\", "/").Replace(courseDirectory + "/", "")).ToList();
                    }

                    courseElement.Contents.Add(content);
                }

                courseJson.Elements.Add(courseElement);
                elementId++;
            }

            string jsonOutput = JsonSerializer.Serialize(courseJson, new JsonSerializerOptions { WriteIndented = true });
            string jsonFilePath = Path.Combine(courseDirectory, $"{courseName}.json");
            await System.IO.File.WriteAllTextAsync(jsonFilePath, jsonOutput);
            Console.WriteLine($"Course JSON generated at: {jsonFilePath}");
        }


    }
}
