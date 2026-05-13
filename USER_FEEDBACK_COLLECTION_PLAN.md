# User Description Feedback Collection — Feature Plan

**Status:** PLANNING
**Objective:** Allow users to flag problematic descriptions, collect patterns for prompt refinement

---

## Problem

Currently no mechanism to collect user feedback on description quality. v15c baseline (0.223 similarity) is based on 10-image evaluation, but real-world usage may reveal different failure patterns.

**Solution:** Add "Report Description" feature in UI + collect feedback in database for later analysis.

---

## User Experience Flow

### Desktop App Changes

**Photo Details Panel** (bottom right when photo selected):

```
┌─────────────────────────────────┐
│ Details                         │
├─────────────────────────────────┤
│ Date: Apr 20, 2026              │
│ Size: 2.5 MB                    │
│ Dimensions: 2048×1536           │
│                                 │
│ AI Description:                 │
│ "Two people sitting on couch    │
│ in living room"                 │
│                                 │
│ [Copy] [Report Issue] ← NEW     │
├─────────────────────────────────┤
│ Tags: person, indoor, furniture │
└─────────────────────────────────┘
```

**Click "Report Issue" → Modal dialog:**

```
┌─────────────────────────────────┐
│ Report Description Issue        │
├─────────────────────────────────┤
│ What's wrong with this          │
│ description?                    │
│                                 │
│ ☐ Hallucinated (fake details)   │
│ ☐ Wrong count of people         │
│ ☐ Wrong gender/pronouns         │
│ ☐ Inaccurate animals            │
│ ☐ Missing important details     │
│ ☐ Wrong setting/location        │
│ ☐ Other (please describe)       │
│                                 │
│ Optional comment:               │
│ ┌──────────────────────────┐    │
│ │                          │    │
│ │                          │    │
│ └──────────────────────────┘    │
│                                 │
│        [Cancel]  [Report]       │
└─────────────────────────────────┘
```

---

## Database Schema

### New Table: DescriptionFeedback

```sql
CREATE TABLE description_feedback (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    media_file_id TEXT NOT NULL,
    prompt_version TEXT NOT NULL,  -- v15c, v15b, etc.
    ai_model_used TEXT NOT NULL,   -- llama3.2-vision, etc.
    issue_categories TEXT NOT NULL,  -- JSON array: ["hallucination", "wrong_count"]
    user_comment TEXT,             -- Optional freeform feedback
    created_at TEXT NOT NULL,      -- ISO datetime
    FOREIGN KEY (media_file_id) REFERENCES media_files(id) ON DELETE CASCADE
);

-- Index for analysis queries
CREATE INDEX IF NOT EXISTS idx_feedback_created_at
    ON description_feedback(created_at DESC);
CREATE INDEX IF NOT EXISTS idx_feedback_prompt_version
    ON description_feedback(prompt_version, issue_categories);
```

### Data Model (C#)

```csharp
public class DescriptionFeedback
{
    public int Id { get; set; }
    public Guid MediaFileId { get; set; }
    public string PromptVersion { get; set; }  // v15c
    public string AiModelUsed { get; set; }    // llama3.2-vision
    public List<string> IssueCategories { get; set; }  // ["hallucination", "wrong_count"]
    public string? UserComment { get; set; }
    public DateTime CreatedAt { get; set; }
}
```

---

## Implementation Components

### Component 1: Database & Repository

**File:** PhotoWell.Data/Repositories/DescriptionFeedbackRepository.cs

```csharp
public class DescriptionFeedbackRepository
{
    private readonly PhotoIQContext _context;

    public DescriptionFeedbackRepository(PhotoIQContext context) => _context = context;

    public async Task<int> AddFeedbackAsync(DescriptionFeedback feedback)
    {
        _context.Add(feedback);
        await _context.SaveChangesAsync();
        return feedback.Id;
    }

    public async Task<List<DescriptionFeedback>> GetFeedbackByPromptVersionAsync(
        string promptVersion, int limit = 100)
        => await _context.Set<DescriptionFeedback>()
            .Where(f => f.PromptVersion == promptVersion)
            .OrderByDescending(f => f.CreatedAt)
            .Take(limit)
            .ToListAsync();

    public async Task<Dictionary<string, int>> GetIssueCategoryCountsAsync(string promptVersion)
    {
        // Parse JSON categories and count occurrences
        var feedback = await GetFeedbackByPromptVersionAsync(promptVersion, int.MaxValue);
        var counts = new Dictionary<string, int>();

        foreach (var fb in feedback)
        {
            foreach (var cat in fb.IssueCategories)
            {
                counts[cat] = counts.TryGetValue(cat, out var count) ? count + 1 : 1;
            }
        }

        return counts;
    }
}
```

### Component 2: Services Layer

**File:** PhotoWell.Services/Feedback/DescriptionFeedbackService.cs

```csharp
public class DescriptionFeedbackService
{
    private readonly DescriptionFeedbackRepository _repo;
    private readonly IMediaFileRepository _mediaRepo;

    public async Task<bool> ReportDescriptionAsync(
        Guid mediaFileId,
        List<string> issueCategories,
        string? userComment = null,
        CancellationToken ct = default)
    {
        var mf = await _mediaRepo.GetByIdAsync(mediaFileId);
        if (mf == null) return false;

        var feedback = new DescriptionFeedback
        {
            MediaFileId = mediaFileId,
            PromptVersion = mf.PromptVersion ?? "unknown",
            AiModelUsed = mf.AiModelUsed ?? "unknown",
            IssueCategories = issueCategories,
            UserComment = userComment?.Trim(),
            CreatedAt = DateTime.UtcNow
        };

        await _repo.AddFeedbackAsync(feedback);
        return true;
    }

    public async Task<FeedbackSummary> GetFeedbackSummaryAsync(string promptVersion)
    {
        var categories = await _repo.GetIssueCategoryCountsAsync(promptVersion);
        var totalCount = categories.Values.Sum();

        return new FeedbackSummary
        {
            PromptVersion = promptVersion,
            TotalReports = totalCount,
            IssueCategoryCounts = categories
        };
    }
}

public class FeedbackSummary
{
    public string PromptVersion { get; set; }
    public int TotalReports { get; set; }
    public Dictionary<string, int> IssueCategoryCounts { get; set; }
}
```

### Component 3: UI Integration

**File:** PhotoWell.Desktop/ViewModels/PhotoDetailsViewModel.cs

```csharp
public class PhotoDetailsViewModel : ObservableObject
{
    private readonly DescriptionFeedbackService _feedbackService;
    private RelayCommand _reportDescriptionCommand;

    public RelayCommand ReportDescriptionCommand
        => _reportDescriptionCommand ??= new RelayCommand(ReportDescription);

    private async void ReportDescription()
    {
        // Show modal dialog (WPF/XAML handles this)
        var dialog = new ReportDescriptionDialog();
        var result = dialog.ShowDialog();

        if (result == true)
        {
            var categories = dialog.SelectedCategories;  // ["hallucination", "wrong_count"]
            var comment = dialog.UserComment;

            var success = await _feedbackService.ReportDescriptionAsync(
                CurrentPhoto.Id, categories, comment);

            if (success)
            {
                // Show confirmation toast
                // "Thank you for your feedback!"
            }
        }
    }
}
```

### Component 4: UI Dialog

**File:** PhotoWell.Desktop/Views/ReportDescriptionDialog.xaml**

```xaml
<Window x:Class="PhotoWell.Desktop.Views.ReportDescriptionDialog"
        Title="Report Description Issue"
        Width="400" Height="350"
        WindowStartupLocation="CenterOwner"
        Background="#1e1e1e">
    <StackPanel Padding="20">
        <TextBlock Text="What's wrong with this description?"
                   Foreground="White" FontSize="14" Margin="0,0,0,15"/>

        <CheckBox Content="Hallucinated (fake details)" Foreground="White" Margin="0,5"/>
        <CheckBox Content="Wrong count of people" Foreground="White" Margin="0,5"/>
        <CheckBox Content="Wrong gender/pronouns" Foreground="White" Margin="0,5"/>
        <CheckBox Content="Inaccurate animals" Foreground="White" Margin="0,5"/>
        <CheckBox Content="Missing important details" Foreground="White" Margin="0,5"/>
        <CheckBox Content="Wrong setting/location" Foreground="White" Margin="0,5"/>
        <CheckBox Content="Other" Foreground="White" Margin="0,5"/>

        <TextBlock Text="Optional comment:" Foreground="White" FontSize="12" Margin="0,15,0,5"/>
        <TextBox MinHeight="60" Foreground="White" Background="#2d2d2d" Padding="10"/>

        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,20,0,0">
            <Button Content="Cancel" Width="80" Margin="0,0,10,0" IsCancel="True"/>
            <Button Content="Report" Width="80" IsDefault="True" Click="Report_Click"/>
        </StackPanel>
    </StackPanel>
</Window>
```

---

## Analytics & Reporting

### SQL Queries for Analysis

**1. Issue patterns by prompt version**
```sql
SELECT prompt_version, COUNT(*) as reports
FROM description_feedback
WHERE created_at >= datetime('now', '-30 days')
GROUP BY prompt_version
ORDER BY reports DESC;
```

**2. Top issue categories**
```sql
SELECT issue_categories, COUNT(*) as count
FROM (
    SELECT json_each.value as issue_categories
    FROM description_feedback,
         json_each(description_feedback.issue_categories)
    WHERE created_at >= datetime('now', '-30 days')
)
GROUP BY issue_categories
ORDER BY count DESC;
```

**3. Photos with multiple complaints**
```sql
SELECT media_file_id, COUNT(*) as complaint_count
FROM description_feedback
WHERE created_at >= datetime('now', '-30 days')
GROUP BY media_file_id
HAVING COUNT(*) >= 3
ORDER BY complaint_count DESC;
```

### Dashboard Report (Monthly)

```
📊 Description Feedback Report — April 2026

Total Reports: 47
By Prompt Version:
  v15c: 47 (100%)

Issue Categories:
  Hallucinated details: 18 (38%)
  Wrong count: 12 (26%)
  Inaccurate animals: 8 (17%)
  Wrong gender: 5 (11%)
  Other: 4 (9%)

Top Issues for v15c:
  1. Hallucinated details — 38%
     → Examples: Added "table", "wine glass" (not in image)
     → Recommendation: Strengthen "WHAT YOU SEE" section in v16

  2. Wrong person count — 26%
     → Examples: Counted game pieces as people
     → Recommendation: Refine "OBJECTS VS. PEOPLE" section

  3. Inaccurate animals — 17%
     → Examples: Orange cat → brown dog
     → Recommendation: Add animal confidence clause to v16
```

---

## Integration with v16 Planning

**Feedback loop:**

1. **Deploy v15c** with feedback collection
2. **Collect 50+ reports** over 2-4 weeks
3. **Analyze patterns** using dashboard
4. **Design v16** addressing top issues
5. **Validate v16** against problem cases
6. **Deploy v16** with improved prompt

**Example:** If "hallucination" is #1 complaint, make that the focus of v16 (add stronger "omit if unclear" guidance).

---

## Timeline

### Phase 1: Database & Backend (1 hour)
- Create DescriptionFeedback table + indexes
- Write DescriptionFeedbackRepository
- Write DescriptionFeedbackService

### Phase 2: UI Integration (1.5 hours)
- Add button to photo details panel
- Create ReportDescriptionDialog (XAML + code-behind)
- Wire up ViewModel command

### Phase 3: Testing & Dashboard (1 hour)
- Unit tests for feedback service
- Manual testing of report flow
- Create SQL dashboard queries

**Total: 3.5 hours**

---

## Success Criteria

**Feature is successful if:**
- ✓ Users can easily report issues (2-click flow)
- ✓ 20+ reports collected within first month
- ✓ Patterns identifiable from issue categories
- ✓ Top issues inform v16 prompt design
- ✓ v16 shows improvement on reported cases

---

## Future Enhancements

**v2:**
- Photo preview in report dialog (remind user what they're reporting on)
- Category suggestions based on description text ("hallucination" highlighted if generic phrase detected)
- Automatic photo attachment (include original photo with report for later analysis)
- Follow-up: "Check v16 — issue fixed?" after deployment
- Leaderboard: "Best helpers" (most useful feedback)

---

## Summary

**User feedback collection:**
- ✓ Non-intrusive (optional "Report" button)
- ✓ Low-friction (5 categories + optional comment)
- ✓ High-value (data for v16 refinement)
- ✓ Low-risk (non-blocking, fire-and-forget)

**Implementation: 3.5 hours, ready to start after test corpus expansion.**
