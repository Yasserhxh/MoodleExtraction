namespace MoodleExtraction.Models;
public class CourseJson
{
    public string? CourseName { get; set; }
    //public string Description { get; set; } = "SVT";
    public string? CourseId { get; set; }
    public string? id { get; set; }
    public string? Photo { get; set; }
    //public string Progession { get; set; } = "0";
    //public string Status { get; set; } = "en cours";
    public string? ProfessorId { get; set; }
    public string? CodeNiveau { get; set; }
    public string? CodeClasse { get; set; }
    public bool IsDownloaded { get; set; } = false;
    public List<ElementProgramme>? ElementProgrammes { get; set; }
    public List<Element>? Elements { get; set; }
}

public class ElementProgramme
{
    public int Level { get; set; }
    public string? Code { get; set; }
}

public class Element
{
    public string? ElementName { get; set; }
    public string? ElementId { get; set; }
    public string? CourseId { get; set; }
    public string? IconBody { get; set; }
    public string? Link { get; set; }
    public bool IsDownloaded { get; set; }
    public List<Content>? contents { get; set; }
}

public class Content
{
    public string? ContentName { get; set; }
    public string? Type { get; set; }
    public List<string>? FilesUrls { get; set; }
    public string? Width { get; set; }
    public string? Height { get; set; }
    public string? Base64 { get; set; }
}
public class ProcessCoursesRequest
{
    public string CoursesPath { get; set; }
    public string H5PPath { get; set; }
}
