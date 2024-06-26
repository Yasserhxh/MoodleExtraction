using Microsoft.AspNetCore.Mvc;
using System.Net;
using System.Text.Json;
using System.Text;
using static MoodleExtraction.Controllers.MoodleController;
using System.Xml;
using System.IO.Compression;
using static MoodleExtraction.Controllers.MoodleClient;
using HtmlAgilityPack;
using System.Diagnostics;
using Microsoft.VisualBasic.FileIO;

using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;


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
           var result =  await _moodleClient.ExtractFrames();

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
                string fileUrl = module.InnerText + "&token=" + token;
                string fileName = moduleName!.InnerText  ;
                byte[] fileContent = await DownloadFileContent(fileUrl);
                courseContent.Add(new CourseContentItem
                {
                    Type = "html",
                    FileName = fileName,
                    Content = fileContent
                });
                Console.WriteLine("one");
            }
        
            if (fileUrlNode != null && fileNameNode != null)
            {
                string fileUrl = fileUrlNode.InnerText + "&token="+ token;
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

            // Switch to the iFrame
            IWebElement iFrame = driver.FindElement(By.Id("mod_hvp_content"));
            driver.SwitchTo().Frame(iFrame);

            // Extract the full HTML content of the iFrame
            string iFrameHtml = driver.FindElement(By.TagName("html")).GetAttribute("outerHTML");

            // Do something with the iFrame HTML content
            Console.WriteLine(iFrameHtml);

            // Close the browser
            driver.Quit();

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
