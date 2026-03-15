using System.ComponentModel.DataAnnotations;

namespace PhotoIQPro.Core.Models;

public class ExclusionRule
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// The folder name or full path to exclude.
    /// </summary>
    [Required]
    [MaxLength(500)]
    public string Value { get; set; } = "";

    /// <summary>
    /// True = match by full path; False = match by folder name anywhere.
    /// </summary>
    public bool IsFullPath { get; set; }
}




