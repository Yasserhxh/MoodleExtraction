using HtmlAgilityPack;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Interactions;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;


namespace MoodleExtraction.Controllers;


[ApiController]
[Route("[controller]")]
public class MoodleController : ControllerBase
{
    private readonly MoodleClient _moodleClient;

    public MoodleController(IHttpClientFactory httpClientFactory)
    {
        var httpClient = httpClientFactory.CreateClient();
        //httpClient.BaseAddress = new Uri("https://m3.inpt.ac.ma");
        _moodleClient = new MoodleClient(httpClient);
    }

    [HttpGet("token")]
    public async Task<IActionResult> GetMoodleToken(string username, string password)
    {
        var token = await _moodleClient.GetTokenAsync(username, password);
        if (!string.IsNullOrEmpty(token))
        {
            return Ok(token);
        }
        else
        {
            return BadRequest("Failed to get Moodle token");
        }
    }

    [HttpGet("courses")]
    public async Task<IActionResult> GetMoodleCourses(string token)
    {
        try
        {
            var courses = await _moodleClient.GetCourses(token);
            return Ok(courses);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error retrieving courses: {ex.Message}");
        }
    }


    [HttpGet("frames")]
    public async Task<IActionResult> GetFrames()
    {
        try
        {
            var result = await _moodleClient.ExtractFrames();

            return Ok(result);
        }
        catch (Exception)
        {

            throw;
        }

    }


    [HttpGet("download/{courseId}")]
    public async Task<IActionResult> DownloadCourse(string token, int courseId)
    {
        try
        {
            //await _moodleClient.Login("alexsys", "Alexsys@24"); 

            var courseContent = await _moodleClient.DownloadCourseContent(token, courseId);
            if (courseContent == null)
            {
                return NotFound($"Course content not found for course with ID {courseId}.");
            }

            string courseDirectory = $"Course_{courseId}_{DateTime.Now:yyyyMMddHHmmss}";
            Directory.CreateDirectory(courseDirectory);

            foreach (var contentItem in courseContent)
            {
                string filePath = Path.Combine(courseDirectory, contentItem.FileName);
                await System.IO.File.WriteAllBytesAsync(filePath, contentItem.Content);
            }

            return Ok($"Course content downloaded to {courseDirectory}.");
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error: {ex.Message}");
        }
    }
    [HttpPost]
    [Route("download-urls")]
    public async Task<IActionResult> DownloadUrls(IFormFile file)
    {

        try
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest("File is empty or not provided.");
            }

            var urls = new List<string>();

            // Read URLs from the uploaded file
            using (var reader = new StreamReader(file.OpenReadStream()))
            {
                while (!reader.EndOfStream)
                {
                    var line = await reader.ReadLineAsync();
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        urls.Add(line.Trim());
                    }
                }
            }

            string courseDirectory = $"Course_{DateTime.Now:yyyyMMddHHmmss}";
            Directory.CreateDirectory(courseDirectory);
            string downloadDirectory = Path.GetFullPath(courseDirectory);

            // Set up ChromeDriver options
            var chromeOptions = new ChromeOptions();
            chromeOptions.AddUserProfilePreference("download.default_directory", downloadDirectory);
            chromeOptions.AddUserProfilePreference("download.prompt_for_download", false);
            chromeOptions.AddUserProfilePreference("disable-popup-blocking", "true");

            using (var driver = new ChromeDriver(chromeOptions))
            {
                driver.Navigate().GoToUrl("https://m3.inpt.ac.ma/login/index.php");
                // Fill in the login credentials and submit the form
                driver.FindElement(By.Id("username")).SendKeys("alexsys");
                driver.FindElement(By.Id("password")).SendKeys("Alexsys@24");
                driver.FindElement(By.Id("loginbtn")).Click();

                await Task.Delay(1000); // Adjust the delay if necessary

                foreach (var url in urls)
                {
                    try
                    {
                        driver.Navigate().GoToUrl(url);
                        // Wait for the download to complete
                        await WaitForDownloadToComplete(downloadDirectory, TimeSpan.FromMinutes(10));
                        // Unzip downloaded files and delete the original zip files
                        UnzipAndCreateIndexHtml(downloadDirectory);
                    }
                    catch (Exception ex)
                    {
                        // Handle exceptions as necessary
                        Console.WriteLine($"Failed to download from URL: {url}. Error: {ex.Message}");
                    }
                }
            }

            return Ok($"Course content and files downloaded to {courseDirectory}.");
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error: {ex.Message}");
        }
    }
    private async Task WaitForDownloadToComplete(string downloadDirectory, TimeSpan timeout)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        while (stopwatch.Elapsed < timeout)
        {
            if (Directory.GetFiles(downloadDirectory, "*.crdownload").Length == 0)
            {
                // No .crdownload files, which means the download is complete
                break;
            }

            await Task.Delay(1000); // Check every second
        }

        stopwatch.Stop();
    }
    private void UnzipAndCreateIndexHtml(string directory)
    {
        foreach (var zipFilePath in Directory.GetFiles(directory, "*.h5p"))
        {
            string extractPath = Path.Combine(directory, Path.GetFileNameWithoutExtension(zipFilePath));
            ZipFile.ExtractToDirectory(zipFilePath, extractPath);
            System.IO.File.Delete(zipFilePath);

            // Create index.html file next to the extracted folder
            string indexPath = Path.Combine(directory, $"index_{Path.GetFileNameWithoutExtension(zipFilePath)}.html");
            string folderName = Path.GetFileName(extractPath);
            string indexContent = $@"
<!DOCTYPE html>
<html lang='en'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>H5P Content Display</title>
    <link rel='stylesheet' href='https://cdn.jsdelivr.net/npm/h5p-standalone@latest/dist/styles/h5p.css'>
</head>
<body>
    <div id='h5p-container'></div>
    <script src='https://code.jquery.com/jquery-3.6.0.min.js'></script>
    <script src='https://cdn.jsdelivr.net/npm/h5p-standalone@latest/dist/main.bundle.js'></script>
    <script>
        const el = document.getElementById('h5p-container');
        const options = {{
            h5pJsonPath: '{folderName}',
            frameJs: 'https://cdn.jsdelivr.net/npm/h5p-standalone@latest/dist/frame.bundle.js',
            frameCss: 'https://cdn.jsdelivr.net/npm/h5p-standalone@latest/dist/styles/h5p.css',
        }};
        new H5PStandalone.H5P(el, options).then(() => {{
            console.log('H5P content loaded successfully.');
        }}).catch(err => {{
            console.error('Failed to load H5P content:', err);
        }});
    </script>
</body>
</html>";

            System.IO.File.WriteAllText(indexPath, indexContent);
        }
    }
}








public class MoodleClient
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUri = "https://m3.inpt.ac.ma";

    public MoodleClient(HttpClient httpClient)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = new System.Net.CookieContainer(),
            UseCookies = true,
            UseDefaultCredentials = false
        };
        _httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(_baseUri)
        };
    }
    public async Task Login(string username, string password)
    {
        var loginData = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("username", username),
            new KeyValuePair<string, string>("password", password)
        });

        var response = await _httpClient.PostAsync("/login/index.php", loginData);
        response.EnsureSuccessStatusCode();
    }


    public async Task<string> GetTokenAsync(string username, string password)
    {
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("username", username),
            new KeyValuePair<string, string>("password", password),
            new KeyValuePair<string, string>("service", "moodle_mobile_app")
        });

        var response = await _httpClient.PostAsync("/login/token.php", content);
        response.EnsureSuccessStatusCode();
        var responseContent = await response.Content.ReadAsStringAsync();
        var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(responseContent);
        return tokenResponse?.token!;
    }


    public async Task<object> GetCourses(string token)
    {
        var response = await _httpClient.GetAsync($"/webservice/rest/server.php?wstoken={token}&wsfunction=core_course_get_courses");
        response.EnsureSuccessStatusCode();
        var responseContent = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<object>(responseContent)!;
    }


    public async Task<List<CourseContentItem>> DownloadCourseContent(string token, int courseId)
    {
        var url = $"/webservice/rest/server.php?wstoken={token}&wsfunction=core_course_get_contents&courseid={courseId}";
        var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var contentString = await response.Content.ReadAsStringAsync();
        var courseContent = new List<CourseContentItem>();

        // Parse the XML response
        var xmlDoc = new XmlDocument();
        xmlDoc.LoadXml(contentString);

        var singleNodes = xmlDoc.SelectNodes("//SINGLE");

        // List to store exportUrls
        List<string> files = new List<string>();
        List<string> exportUrls = new List<string>();
        foreach (XmlNode singleNode in singleNodes!)
        {
            var typeNode = singleNode.SelectSingleNode("KEY[@name='type']/VALUE");
            var fileNameNode = singleNode.SelectSingleNode("KEY[@name='filename']/VALUE");
            var fileUrlNode = singleNode.SelectSingleNode("KEY[@name='fileurl']/VALUE");
            var fileSizeNode = singleNode.SelectSingleNode("KEY[@name='filesize']/VALUE");
            var contentNode = singleNode.SelectSingleNode("KEY[@name='content']/VALUE");
            var module = singleNode.SelectSingleNode("KEY[@name='url']/VALUE");
            var moduleName = singleNode.SelectSingleNode("KEY[@name='name']/VALUE");

            if (module != null && module.InnerText.Contains("hvp"))
            {
               
                string fileUrl = module.InnerText;
                // Initialize ChromeDriver and navigate to the page
                var options = new ChromeOptions();
                options.AddArgument("--headless"); // Run in headless mode (no GUI)
                using var driver = new ChromeDriver(options);
                // Navigate to the login page
                driver.Navigate().GoToUrl("https://m3.inpt.ac.ma/login/index.php");
                // Fill in the login credentials and submit the form
                driver.FindElement(By.Id("username")).SendKeys("alexsys");
                driver.FindElement(By.Id("password")).SendKeys("Alexsys@24");
                driver.FindElement(By.Id("loginbtn")).Click();
                // Wait for the login process to complete and redirect
                await
                Task.Delay(1000);
                // Example: wait for 3 seconds (consider using WebDriverWait)
                driver.Navigate().GoToUrl(fileUrl); // Replace with your URL

                // Get all <script> elements
                var scriptElements = driver.FindElements(By.TagName("script"));

                // Iterate through each <script> element
                foreach (var scriptElement in scriptElements)
                {
                    var scriptText = scriptElement.GetAttribute("innerHTML");
                    if (scriptText.Contains("var H5PIntegration = "))
                    {
                        Console.WriteLine("Found H5PIntegration script:");
                        Console.WriteLine(scriptText);

                        string startPattern = "\r\n//<![CDATA[\r\nvar H5PIntegration = ";
                        string endPattern = ";\r\n//]]>";

                        // Extract JSON substring
                        int startIndex = scriptText.IndexOf(startPattern) + startPattern.Length;
                        int endIndex = scriptText.IndexOf(endPattern, startIndex);
                        string jsonSubstring = scriptText.Substring(startIndex, endIndex - startIndex).Trim();

                        // Deserialize JSON substring into JObject
                        JsonNode jsonObject = JsonNode.Parse(jsonSubstring);


                        FindExportUrls(jsonObject["contents"], exportUrls);




                        // Access the value of "exportUrl" under "contents"
                        //string exportUrl = (string)jsonObject["contents"]["cid-76"]["exportUrl"];
                        //files.Add(exportUrl);

                        break; // Exit loop if found
                    }
                }

                // Save exportUrls to a text file
                string filePath = "exportUrls.txt";
                using (StreamWriter writer = new StreamWriter(filePath))
                {
                    foreach (var item in exportUrls)
                    {
                        writer.WriteLine(item);
                    }
                }

                Console.WriteLine($"Exported {files.Count} URLs to {filePath}");
            }

            if (fileUrlNode != null && fileNameNode != null)
            {
                string fileUrl = fileUrlNode.InnerText + "&token=" + token;
                string fileName = fileNameNode.InnerText;

                string fileType = typeNode != null ? typeNode.InnerText : string.Empty;
                if (fileType == "file")
                {
                    byte[] fileContent = await DownloadFileContent(fileUrl);

                    courseContent.Add(new CourseContentItem
                    {
                        Type = fileType,
                        FileName = fileName,
                        Content = fileContent
                    });
                }
            }
        }
        return courseContent;


    }

    public void FindExportUrls(JsonNode node, List<string> exportUrls)
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
    public async Task<HtmlDocument> ExtractFrames()
    {
        // Étape 1 : Lancement du navigateur Chrome
        var options = new ChromeOptions();
        options.AddArgument("--headless"); // Exécution en mode sans interface graphique

        using (var driver = new ChromeDriver(options))
        {
            // Navigate to the login page
            driver.Navigate().GoToUrl("https://m3.inpt.ac.ma/login/index.php");
            // Fill in the login credentials and submit the form
            driver.FindElement(By.Id("username")).SendKeys("alexsys");
            driver.FindElement(By.Id("password")).SendKeys("Alexsys@24");
            driver.FindElement(By.Id("loginbtn")).Click();
            // Navigate to the protected page with the iFrame
            driver.Navigate().GoToUrl("https://m3.inpt.ac.ma/mod/hvp/view.php?id=480");
            IWebElement iBody = driver.FindElement(By.Id("page-content"));

            var element = driver.FindElement(By.Id("page-content")).GetAttribute("outerHTML");
            //Switch to the iFrame
            IWebElement iFrame = driver.FindElement(By.Id("h5p-iframe-76"));

            driver.SwitchTo().Frame(iFrame);
            try
            {
                while (driver.FindElement(By.ClassName("h5p-dialogcards-next")) != null)
                {
                    driver.FindElement(By.ClassName("h5p-dialogcards-next")).Click();
                }
            }
            finally
            {
                string iFrameHtml = driver.FindElement(By.TagName("html")).GetAttribute("outerHTML");

                // Fetch the content of the iframe
                string iframeContent = iFrameHtml;


                

                // Create a new HTML document with the iframe content
                string newHtml = $@"
                <!DOCTYPE html>
                <html>
                <head>

                    <title>New Iframe</title>
                    <script src='Happy/h5p-action-bar.js'></script>
                    <script src='Happy/h5p-confirmation-dialog.js'></script>
                    <script src='Happy/h5p-content-type.js'></script>
                    <script src='Happy/h5p-content-upgrade.js'></script>
                    <script src='Happy/h5p-data-view.js'></script>
                    <script src='Happy/h5p-display-options.js'></script>
                    <script src='Happy/h5p-embed.js'></script>
                    <script src='Happy/h5p-event-dispatcher.js'></script>
                    <script src='Happy/h5p-hub-sharing.js'></script>
                    <script src='Happy/h5p-library-list.js'></script>
                    <script src='Happy/h5p-resizer.js'></script>
                    <script src='Happy/h5p-tooltip.js'></script>
                    <script src='Happy/h5p-utils.js'></script>
                    <script src='Happy/h5p-version.js'></script>
                    <script src='Happy/h5p-x-api.js'></script>
                    <script src='Happy/h5p.js'></script>
                </head>
                <body>
                    {iframeContent}
                </body>
                </html>
                ";

                // Save the new HTML document as a file
                string newIframeFilePath = "new_iframe.html";
                File.WriteAllText(newIframeFilePath, newHtml);

                // You can now use the new iframe file in your .NET application
                string iframeTag = $"<iframe src=\"{newIframeFilePath}\"></iframe>";

                // Create a new iframe element
                //IWebElement newIframe = driver.FindElement(By.TagName("body")).FindElement(By.TagName("iframe"));
            }
        }
        return null;
    }


    private async Task<byte[]> DownloadFileContent(string fileUrl)
    {
        var response = await _httpClient.GetAsync(fileUrl);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync();
    }




    private class TokenResponse
    {
        public string? token { get; set; }
    }
    public class CourseContentItem
    {
        public string? Type { get; set; }
        public string? FileName { get; set; }
        public byte[]? Content { get; set; }
    }

}
