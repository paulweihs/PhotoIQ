using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PhotoIQPro.Desktop.ViewModels;
using PhotoIQPro.Core.Models;
using Xunit;

namespace PhotoIQPro.Tests;

/// <summary>Tests for the "On This Day" feature: anniversary date matching and filtering.</summary>
public class OnThisDayViewModelTests
{
    private static MediaFile Photo(int year, int month, int day, int hour = 12)
        => new MediaFile
        {
            Id        = Guid.NewGuid(),
            FileName  = $"{year}-{month:D2}-{day:D2}.jpg",
            Extension = ".jpg",
            DateTaken = new DateTime(year, month, day, hour, 0, 0),
            FilePath  = $"C:\\fake\\{year}-{month:D2}-{day:D2}.jpg",
        };

    [Fact]
    public async Task LoadAsync_GroupsByYearDescending()
    {
        var vm = new OnThisDayViewModel();
        var today = new DateTime(2026, 4, 14);
        var photos = new List<MediaFile>
        {
            Photo(2023, 4, 14),
            Photo(2021, 4, 14),
            Photo(2024, 4, 14),
        };

        await vm.LoadAsync(photos, today);

        Assert.Equal(3, vm.Groups.Count);
        Assert.Equal(2024, vm.Groups[0].Year);
        Assert.Equal(2023, vm.Groups[1].Year);
        Assert.Equal(2021, vm.Groups[2].Year);
    }

    [Fact]
    public async Task LoadAsync_EmptyList_HasNoMemories()
    {
        var vm = new OnThisDayViewModel();
        await vm.LoadAsync([], new DateTime(2026, 4, 14));

        Assert.False(vm.HasMemories);
        Assert.Empty(vm.Groups);
    }

    [Fact]
    public async Task LoadAsync_SetsHeadingText()
    {
        var vm = new OnThisDayViewModel();
        await vm.LoadAsync([], new DateTime(2026, 6, 3));

        Assert.Equal("June 3", vm.HeadingText);
    }

    [Fact]
    public async Task LoadAsync_OrdersPhotosWithinYearByTime()
    {
        var vm = new OnThisDayViewModel();
        var photos = new List<MediaFile>
        {
            Photo(2023, 4, 14, hour: 15),
            Photo(2023, 4, 14, hour: 9),
            Photo(2023, 4, 14, hour: 12),
        };

        await vm.LoadAsync(photos, new DateTime(2026, 4, 14));

        var group = vm.Groups[0];
        Assert.Equal(9,  group.Photos[0].DateTaken!.Value.Hour);
        Assert.Equal(12, group.Photos[1].DateTaken!.Value.Hour);
        Assert.Equal(15, group.Photos[2].DateTaken!.Value.Hour);
    }

    [Fact]
    public async Task LoadAsync_GroupLabel_SinglePhoto()
    {
        var vm = new OnThisDayViewModel();
        await vm.LoadAsync([Photo(2022, 4, 14)], new DateTime(2026, 4, 14));

        Assert.Equal("2022  ·  1 photo", vm.Groups[0].Label);
    }

    [Fact]
    public async Task LoadAsync_GroupLabel_MultiplePhotos()
    {
        var vm = new OnThisDayViewModel();
        await vm.LoadAsync([Photo(2022, 4, 14), Photo(2022, 4, 14, 14)], new DateTime(2026, 4, 14));

        Assert.Equal("2022  ·  2 photos", vm.Groups[0].Label);
    }
}

public class TagsBrowserViewModelFilterTests
{
    [Fact]
    public void TagEntry_HasNameAndCount()
    {
        var entry = new TagEntry { Name = "sunset", PhotoCount = 42 };
        Assert.Equal("sunset", entry.Name);
        Assert.Equal(42, entry.PhotoCount);
    }
}

public class PersonCardViewModelTests
{
    private static Person MakePerson(string? name = null, bool isNamed = false)
        => new Person
        {
            Id             = Guid.NewGuid(),
            Name           = name,
            NormalizedName = name?.ToLowerInvariant(),
            IsNamed        = isNamed,
        };

    [Fact]
    public void Constructor_UnnamedPerson_DisplaysUnknown()
    {
        var card = new PersonCardViewModel(MakePerson(), null, 5);
        Assert.Equal("Unknown", card.DisplayName);
        Assert.False(card.IsNamed);
        Assert.Equal(5, card.PhotoCount);
    }

    [Fact]
    public void Constructor_NamedPerson_DisplaysName()
    {
        var card = new PersonCardViewModel(MakePerson("Alice", true), null, 3);
        Assert.Equal("Alice", card.DisplayName);
        Assert.True(card.IsNamed);
    }

    [Fact]
    public void ApplyName_UpdatesDisplayAndIsNamed()
    {
        var card = new PersonCardViewModel(MakePerson(), null, 1);
        card.ApplyName("Bob");

        Assert.Equal("Bob", card.DisplayName);
        Assert.True(card.IsNamed);
        Assert.Equal("Bob", card.EditName);
        Assert.False(card.IsEditing);
    }

    [Fact]
    public void Constructor_EditName_EmptyForUnknown()
    {
        var card = new PersonCardViewModel(MakePerson(), null, 0);
        Assert.Equal("", card.EditName);
    }
}

public class FaceItemViewModelTests
{
    private static Face MakeFace(double x, double y, double w, double h, Guid? personId = null)
        => new Face
        {
            Id          = Guid.NewGuid(),
            PersonId    = personId,
            X           = x,
            Y           = y,
            Width       = w,
            Height      = h,
            MediaFileId = Guid.NewGuid(),
        };

    [Fact]
    public void Constructor_StoresNormalizedCoordinates()
    {
        var face = MakeFace(0.1, 0.2, 0.3, 0.4);
        var vm = new FaceItemViewModel(face, null);

        Assert.Equal(0.1, vm.X);
        Assert.Equal(0.2, vm.Y);
        Assert.Equal(0.3, vm.FaceWidth);
        Assert.Equal(0.4, vm.FaceHeight);
    }

    [Fact]
    public void Constructor_WithPersonName_IsNamed()
    {
        var face = MakeFace(0.1, 0.2, 0.3, 0.4, Guid.NewGuid());
        var vm = new FaceItemViewModel(face, "Alice");

        Assert.True(vm.IsNamed);
        Assert.Equal("Alice", vm.MatchedName);
        Assert.Equal("Alice", vm.NameInput);
    }

    [Fact]
    public void Constructor_WithoutPersonName_IsNotNamed()
    {
        var face = MakeFace(0.1, 0.2, 0.3, 0.4);
        var vm = new FaceItemViewModel(face, null);

        Assert.False(vm.IsNamed);
        Assert.Null(vm.MatchedName);
        Assert.Equal("", vm.NameInput);
    }

    [Fact]
    public void FaceOverlayMath_CorrectlyProjects_CenteredImage()
    {
        // Canvas 800x600, image 1600x1200 (same aspect ratio — no letterbox bars)
        // Scale = min(800/1600, 600/1200) = 0.5
        // rendW=800, rendH=600, offsetX=0, offsetY=0
        double canvasW = 800, canvasH = 600;
        double imgW    = 1600, imgH   = 1200;
        double scale   = Math.Min(canvasW / imgW, canvasH / imgH);
        double rendW   = imgW * scale;
        double rendH   = imgH * scale;
        double offsetX = (canvasW - rendW) / 2.0;
        double offsetY = (canvasH - rendH) / 2.0;

        var face = MakeFace(x: 0.25, y: 0.25, w: 0.1, h: 0.1);
        double left = offsetX + face.X * rendW;
        double top  = offsetY + face.Y * rendH;
        double w    = face.Width  * rendW;
        double h    = face.Height * rendH;

        Assert.Equal(0.5,   scale,   precision: 5);
        Assert.Equal(0.0,   offsetX, precision: 5);
        Assert.Equal(0.0,   offsetY, precision: 5);
        Assert.Equal(200.0, left,    precision: 5);
        Assert.Equal(150.0, top,     precision: 5);
        Assert.Equal(80.0,  w,       precision: 5);
        Assert.Equal(60.0,  h,       precision: 5);
    }

    [Fact]
    public void FaceOverlayMath_CorrectlyProjects_LetterboxedImage()
    {
        // Canvas 800x600, image 1200x400 (3:1 aspect)
        // Scale = min(800/1200, 600/400) = min(0.667, 1.5) = 0.667
        // rendW=800, rendH=267, offsetX=0, offsetY=(600-267)/2=166.5
        double canvasW = 800, canvasH = 600;
        double imgW    = 1200, imgH   = 400;
        double scale   = Math.Min(canvasW / imgW, canvasH / imgH);
        double rendW   = imgW * scale;
        double rendH   = imgH * scale;
        double offsetX = (canvasW - rendW) / 2.0;
        double offsetY = (canvasH - rendH) / 2.0;

        // Face at top-left of image (0.1, 0.1, 0.2, 0.2)
        var face = MakeFace(x: 0.1, y: 0.1, w: 0.2, h: 0.2);
        double left = offsetX + face.X * rendW;
        double top  = offsetY + face.Y * rendH;

        // offsetX should be 0 (fits full width), offsetY > 0 (pillarboxed vertically)
        Assert.Equal(0.0, offsetX, precision: 3);
        Assert.True(offsetY > 0, "Wide image should have vertical letterbox bars");
        Assert.True(left  > 0, "Face left should be > 0");
        Assert.True(top > offsetY, "Face top should be below the letterbox bar");
    }
}
