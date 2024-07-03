﻿using HtmlAgilityPack;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using System.Text.Json;
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
                using (var driver = new ChromeDriver(options))
                {
                    // Navigate to the login page
                    driver.Navigate().GoToUrl("https://m3.inpt.ac.ma/login/index.php");
                    // Fill in the login credentials and submit the form
                    driver.FindElement(By.Id("username")).SendKeys("alexsys");
                    driver.FindElement(By.Id("password")).SendKeys("Alexsys@24");
                    driver.FindElement(By.Id("loginbtn")).Click();
                    // Wait for the login process to complete and redirect
                    await
                    Task.Delay(3000);
                    // Example: wait for 3 seconds (consider using WebDriverWait)
                    driver.Navigate().GoToUrl(fileUrl); // Replace with your URL

                    // Get all <script> elements
                    var scriptElements = driver.FindElements(By.TagName("script"));

                    // List to store exportUrls
                    List<string> files = new List<string>();

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
                            JObject jsonObject = JObject.Parse(jsonSubstring);

                            // Access the value of "exportUrl" under "contents"
                            string exportUrl = (string)jsonObject["contents"]["cid-76"]["exportUrl"];
                            files.Add(exportUrl);

                            break; // Exit loop if found
                        }
                    }

                    // Save exportUrls to a text file
                    string filePath = "exportUrls.txt";
                    using (StreamWriter writer = new StreamWriter(filePath))
                    {
                        foreach (var item in files)
                        {
                            writer.WriteLine(item);
                        }
                    }

                    Console.WriteLine($"Exported {files.Count} URLs to {filePath}");
                }
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
            /*      if (contentNode != null && !string.IsNullOrEmpty(contentNode.InnerText))
                  {
                      var htmlDoc = new HtmlDocument();
                      htmlDoc.LoadHtml(contentNode.InnerText);

                      var h5pLinks = htmlDoc.DocumentNode.SelectNodes("//div[contains(@class, 'h5p-content')]/iframe");
                      if (h5pLinks != null)
                      {
                          foreach (var link in h5pLinks)
                          {
                              string h5pUrl = link.GetAttributeValue("src", string.Empty);
                              if (!string.IsNullOrEmpty(h5pUrl))
                              {
                                  // Download H5P content (this might need to be adapted based on actual content type and handling requirements)
                                  byte[] h5pContent = await DownloadFileContent(h5pUrl + (h5pUrl.Contains("?") ? "&" : "?") + "token=" + token);
                                  courseContent.Add(new CourseContentItem
                                  {
                                      Type = "H5P",
                                      FileName = "H5PContent.html",  // You may want to generate a meaningful name based on the content
                                      Content = h5pContent
                                  });
                              }
                          }
                      }
                  }
            */
        }
        return courseContent;


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
