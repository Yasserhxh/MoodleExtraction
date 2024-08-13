using Microsoft.AspNetCore.Mvc;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using SeleniumExtras.WaitHelpers;
using OfficeOpenXml;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

[ApiController]
[Route("[controller]")]
public class CourseScraperController : ControllerBase
{
    [HttpGet("scrape-courses")]
    public async Task<IActionResult> ScrapeCourses()
    {
        var options = new ChromeOptions();
        // options.AddArgument("--headless"); // Run in headless mode (no GUI)

        List<Course> courses = new List<Course>();

        using (var driver = new ChromeDriver(options))
        {
            // Step 1: Login
            driver.Navigate().GoToUrl("https://m3.inpt.ac.ma/login/index.php");
            driver.FindElement(By.Id("username")).SendKeys("alexsys");
            driver.FindElement(By.Id("password")).SendKeys("Alexsys@24");
            driver.FindElement(By.Id("loginbtn")).Click();

            // Wait for login to complete
            WebDriverWait wait = new WebDriverWait(driver, TimeSpan.FromSeconds(10));
            wait.Until(ExpectedConditions.UrlContains("my/"));

            // Step 2: Navigate to the course listing page
            driver.Navigate().GoToUrl("https://m3.inpt.ac.ma/course/index.php");

            bool hasNextPage = true;
            int currentPage = 1;

            while (hasNextPage)
            {
                try
                {
                    // Wait for the container to be fully loaded (loading class removed)
                    wait.Until(d => !d.FindElement(By.CssSelector("div.courses-container-inner")).GetAttribute("class").Contains("loading"));

                    // Step 3: Scrape course details
                    var courseItems = driver.FindElements(By.CssSelector("div.courses-container div.theme-course-item-inner"));

                    foreach (var courseItem in courseItems)
                    {
                        try
                        {
                            var courseLinkElement = courseItem.FindElement(By.CssSelector("h4.title a"));
                            string courseName = courseLinkElement.Text;
                            string courseUrl = courseLinkElement.GetAttribute("href");

                            int courseId = int.Parse(courseUrl.Split("id=")[1]);

                            // Open the course link in a new tab
                            ((IJavaScriptExecutor)driver).ExecuteScript("window.open();");
                            driver.SwitchTo().Window(driver.WindowHandles.Last());
                            driver.Navigate().GoToUrl(courseUrl);

                            // Wait for the page to load
                            wait.Until(ExpectedConditions.ElementIsVisible(By.CssSelector("nav[aria-label='Barre de navigation'] ol.breadcrumb")));

                            var breadcrumbItems = driver.FindElements(By.CssSelector("nav[aria-label='Barre de navigation'] ol.breadcrumb li"));

                            string annee = breadcrumbItems.Count > 2 ? breadcrumbItems[2].Text : courseName;
                            string matiere = breadcrumbItems.Count > 3 ? breadcrumbItems[3].Text : courseName;

                            // Add the course details to the list
                            courses.Add(new Course
                            {
                                CourseName = courseName,
                                CourseId = courseId,
                                Annee = annee,
                                Matiere = matiere
                            });

                            // Close the current tab
                            driver.Close();

                            // Switch back to the original tab
                            driver.SwitchTo().Window(driver.WindowHandles.First());
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error processing course: {ex.Message}");
                            continue; // Skip this course and continue with the next
                        }
                    }

                    // Step 4: Handle pagination
                    try
                    {
                        var nextPageButton = driver.FindElement(By.CssSelector("ul.theme-courses-pagin-list li.theme-courses-paginitem.next"));
                        nextPageButton.Click();
                        currentPage++;
                        wait.Until(d => !d.FindElement(By.CssSelector("div.courses-container-inner")).GetAttribute("class").Contains("loading"));
                    }
                    catch (NoSuchElementException)
                    {
                        hasNextPage = false; // No more pages
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error on page {currentPage}: {ex.Message}");
                    hasNextPage = false;
                }
            }
        }

        // After scraping, save the courses to an Excel file
        SaveCoursesToExcel(courses);

        return Ok(courses);
    }

    private void SaveCoursesToExcel(List<Course> courses)
    {
        // Set the license context for EPPlus
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

        // Remove duplicates based on CourseId
        var distinctCourses = courses.GroupBy(c => c.CourseId)
                                     .Select(g => g.First())
                                     .ToList();

        string filePath = "Courses.xlsx";
        FileInfo fileInfo = new FileInfo(filePath);

        if (fileInfo.Exists)
        {
            fileInfo.Delete();
        }

        using (ExcelPackage package = new ExcelPackage(fileInfo))
        {
            ExcelWorksheet worksheet = package.Workbook.Worksheets.Add("Courses");

            // Set up the header row
            worksheet.Cells[1, 1].Value = "Course Name";
            worksheet.Cells[1, 2].Value = "Course ID";
            worksheet.Cells[1, 3].Value = "Annee";
            worksheet.Cells[1, 4].Value = "Matiere";

            // Populate the worksheet with course data
            int row = 2;
            foreach (var course in distinctCourses)
            {
                worksheet.Cells[row, 1].Value = course.CourseName;
                worksheet.Cells[row, 2].Value = course.CourseId;
                worksheet.Cells[row, 3].Value = course.Annee;
                worksheet.Cells[row, 4].Value = course.Matiere;
                row++;
            }

            // Apply some styling
            worksheet.Cells[1, 1, 1, 4].Style.Font.Bold = true;
            worksheet.Cells[worksheet.Dimension.Address].AutoFitColumns();

            // Save the package
            package.Save();
        }

        Console.WriteLine($"Courses have been saved to {filePath}");
    }

    public class Course
    {
        public string CourseName { get; set; }
        public int CourseId { get; set; }
        public string Annee { get; set; }
        public string Matiere { get; set; }
    }
}
