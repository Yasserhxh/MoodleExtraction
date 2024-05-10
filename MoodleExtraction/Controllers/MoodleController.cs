using Microsoft.AspNetCore.Mvc;
using System.Net;
using System.Text.Json;
using System.Text;
using static MoodleExtraction.Controllers.MoodleController;
using System.Xml;

namespace MoodleExtraction.Controllers;
[ApiController]
[Route("[controller]")]
public class MoodleController : ControllerBase
{
    private readonly HttpClient _httpClient;

    public MoodleController(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.BaseAddress = new Uri("https://m3.inpt.ac.ma");
    }

    [HttpGet("token")]
    public async Task<IActionResult> GetMoodleToken(string username, string password)
    {
        try
        {
            var token = await GetTokenFromMoodle(username, password);

            if (!string.IsNullOrEmpty(token))
            {
                return Ok(token);
            }
            else
            {
                return BadRequest("Failed to get Moodle token");
            }
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error: {ex.Message}");
        }
    }

    [HttpGet("courses")]
    public async Task<IActionResult> GetMoodleCourses(string token)
    {
        try
        {
            var courses = await GetCoursesFromMoodle(token, "1");

            return Ok(courses);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error: {ex.Message}");
        }
    }

    private async Task<string> GetTokenFromMoodle(string username, string password)
    {
        try
        {
            // Prepare Moodle token request
            var content = new StringContent($"username={username}&password={password}&service=moodle_mobile_app", Encoding.UTF8, "application/x-www-form-urlencoded");

            // Send request to Moodle API
            var response = await _httpClient.PostAsync("/login/token.php", content);

            // Check if request was successful
            response.EnsureSuccessStatusCode();

            // Read response content
            var responseContent = await response.Content.ReadAsStringAsync();
            var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(responseContent);

            return tokenResponse?.token;
        }
        catch (Exception ex)
        {
            throw new Exception($"Error getting token from Moodle: {ex.Message}");
        }
    }

    private async Task<List<Course>> GetCoursesFromMoodle(string token,string courseId)
    {
        try
        {
            // Prepare Moodle courses request
            var response = await _httpClient.GetAsync($"/webservice/rest/server.php?wstoken={token}&wsfunction=core_course_get_courses&options[ids][0]={courseId}");

            // Check if request was successful
            response.EnsureSuccessStatusCode();

            // Read response content
            var responseContent = await response.Content.ReadAsStringAsync();
            var coursesResponse = JsonSerializer.Deserialize<CoursesResponse>(responseContent);

            return coursesResponse?.courses;
        }
        catch (Exception ex)
        {
            throw new Exception($"Error getting courses from Moodle: {ex.Message}");
        }
    }
    [HttpGet("download")]
    public async Task<IActionResult> DownloadCourse(string token, int courseId)
    {
        try
        {
            var courseContent = await GetCourseContent(token, courseId);
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
    [HttpGet("coursewithdata")]
    public async Task<IActionResult> GetCourseWithData(string token)
    {
        try
        {
            var courses = await GetMoodleCoursesWithData(token);

            return Ok(courses);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error: {ex.Message}");
        }
    }
    private async Task<List<Course>> GetMoodleCoursesWithData(string token)
    {
        try
        {

            // Get user ID
           // var userId = await GetUserId(token);

            // Prepare Moodle courses request for the current user
            var response = await _httpClient.GetAsync($"/webservice/rest/server.php?wstoken={token}&wsfunction=core_enrol_get_users_courses&userid={298}");

            // Check if request was successful
            response.EnsureSuccessStatusCode();

            // Read response content
            var responseContent = await response.Content.ReadAsStringAsync();
            var coursesResponse = JsonSerializer.Deserialize<CoursesResponse>(responseContent);

            return coursesResponse.courses;
        }
        catch (Exception ex)
        {
            throw new Exception($"Error getting courses from Moodle: {ex.Message}");
        }
    }
    private async Task<int> GetUserId(string token)
    {
        try
        {
            // Prepare Moodle site info request
            var response = await _httpClient.GetAsync($"/webservice/rest/server.php?wstoken={token}&wsfunction=core_webservice_get_site_info");

            // Check if request was successful
            response.EnsureSuccessStatusCode();

            // Read response content
            var responseContent = await response.Content.ReadAsStringAsync();
            var siteInfoResponse = JsonSerializer.Deserialize<SiteInfoResponse>(responseContent);

            return siteInfoResponse.userid;
        }
        catch (Exception ex)
        {
            throw new Exception($"Error getting user ID from Moodle: {ex.Message}");
        }
    }
    private async Task<List<CourseContentItem>> GetCourseContent(string token, int courseId)
    {
        try
        {
            // Login to Moodle
            MoodleClient client = new MoodleClient("https://m3.inpt.ac.ma");
            await client.LoginAsync("alexsys", "Alexsys@24");
            // Prepare Moodle course content request
            var response = await _httpClient.GetAsync($"/webservice/rest/server.php?wstoken={token}&wsfunction=core_course_get_contents&courseid={courseId}");

            // Check if request was successful
            response.EnsureSuccessStatusCode();

            // Read response content
            var responseContent = await response.Content.ReadAsStringAsync();
            var courseContent = new List<CourseContentItem>();

            // Parse XML response
            var xmlDoc = new XmlDocument();
            xmlDoc.LoadXml(responseContent);
            var contentNodes = xmlDoc.SelectNodes("//RESPONSE/MULTIPLE/SINGLE");
            foreach (XmlNode contentNode in contentNodes)
            {
                var modulesNode = contentNode.SelectSingleNode("KEY[@name='modules']");
                if (modulesNode != null)
                {
                    var moduleNodes = modulesNode.SelectNodes("MULTIPLE/SINGLE");
                    foreach (XmlNode moduleNode in moduleNodes)
                    {
                        var moduleUrlNode = moduleNode.SelectSingleNode("KEY[@name='url']/VALUE");
                        var moduleNameNode = moduleNode.SelectSingleNode("KEY[@name='name']/VALUE");
                        if (moduleUrlNode != null && moduleNameNode != null)
                        {
                            string moduleUrl = moduleUrlNode.InnerText;
                            string moduleName = moduleNameNode.InnerText;
                            // Download module content
                            byte[] moduleContents = await client.DownloadModuleContent(moduleUrl);

                            // Convert byte[] to string
                            string htmlContent = Encoding.UTF8.GetString(moduleContents);

                            byte[] moduleContent = await DownloadModuleContent(moduleUrl);
                            courseContent.Add(new CourseContentItem
                            {
                                Type = "Module",
                                FileName = moduleName,
                                Content = moduleContent
                            });
                        }
                    }
                }

                var summaryNode = contentNode.SelectSingleNode("KEY[@name='summary']/VALUE");
                if (summaryNode != null && !string.IsNullOrEmpty(summaryNode.InnerText))
                {
                    courseContent.Add(new CourseContentItem
                    {
                        Type = "Summary",
                        FileName = "summary.txt",
                        Content = Encoding.UTF8.GetBytes(summaryNode.InnerText)
                    });
                }
            }

            return courseContent;
        }
        catch (Exception ex)
        {
            throw new Exception($"Error getting course content from Moodle: {ex.Message}");
        }
    }
    private async Task<byte[]> DownloadModuleContent(string moduleUrl)
    {
        var response = await _httpClient.GetAsync(moduleUrl);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync();
    }
    // Classes for deserialization
    public class TokenResponse
    {
        public string token { get; set; }
    }

    public class CoursesResponse
    {
        public List<Course> courses { get; set; }
    }

    public class Course
    {
        public int id { get; set; }
        public string fullname { get; set; }
    }
    public class CourseContentItem
    {
        public string Type { get; set; }
        public string FileName { get; set; }
        public byte[] Content { get; set; }
    }
    public class UsersResponse
    {
        public List<User> users { get; set; }
    }

    public class User
    {
        public int id { get; set; }
        public string username { get; set; }
        // Add other user properties as needed
    }
    public class SiteInfoResponse
    {
        public int userid { get; set; }
        // Add other properties as needed
    }







    class MoodleClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _moodleUrl;
        private string _token;

        public MoodleClient(string moodleUrl)
        {
            _httpClient = new HttpClient();
            _moodleUrl = moodleUrl.TrimEnd('/');
        }

        public async Task LoginAsync(string username, string password)
        {
            string loginUrl = $"{_moodleUrl}/login/token.php";

            var parameters = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("username", username),
                new KeyValuePair<string, string>("password", password),
                new KeyValuePair<string, string>("service", "moodle_mobile_app")
            });

            var response = await _httpClient.PostAsync(loginUrl, parameters);
            response.EnsureSuccessStatusCode();

            string responseString = await response.Content.ReadAsStringAsync();
            _token = responseString.Trim('"');
        }

        public async Task<byte[]> DownloadModuleContent(string moduleUrl)
        {
            if (string.IsNullOrEmpty(_token))
            {
                throw new InvalidOperationException("You must login first");
            }

            string fullUrl = $"{moduleUrl}&token={_token}";
            var response = await _httpClient.GetAsync(fullUrl);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadAsByteArrayAsync();
        }
    }
}
