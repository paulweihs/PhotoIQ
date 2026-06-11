#nullable disable // decompiler artifact — nullability annotations were lost when MainViewModel.cs was recovered from the compiled DLL
#pragma warning disable CS4014 // fire-and-forget tasks are intentional in this file
#pragma warning disable CS8632 // nullable annotation syntax in #nullable disable context — decompiler artifact
using System;
using System.CodeDom.Compiler;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using PhotoWell.Common;
using PhotoWell.Core.Interfaces;
using PhotoWell.Core.Models;
using PhotoWell.Desktop.Views;
using PhotoWell.Services.Import;
using PhotoWell.Services.Search;

namespace PhotoWell.Desktop.ViewModels;

public class MainViewModel : ObservableObject, IDisposable, IAssistantActions
{
	private sealed class LockReleaser(SemaphoreSlim semaphore, string caller) : IDisposable
	{
		public void Dispose()
		{
			AppLog.Info("[LOCK] " + caller + ": releasing _analysisLock");
			semaphore.Release();
		}
	}

	private readonly IServiceScopeFactory _scopeFactory;

	private readonly ISemanticSearchService _semanticSearch;

	private readonly IFolderWatcherService _folderWatcher;

	private readonly IExifEditService _exifEditService;

	/// <summary>The currently open photo viewer VM, or null when the viewer is closed.</summary>
	private PhotoViewerViewModel? _activeViewerVm;

	private readonly HashSet<string> _excludedFullPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private readonly HashSet<string> _excludedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private ObservableCollection<MediaFile> _mediaFiles = new ObservableCollection<MediaFile>();

	private MediaFile? _selectedMediaFile;

	private string _statusText = "Loading library…";

	private bool _initialLoadDone;

	private int _photoCount;

	private int _totalLibraryCount;

	private ObservableCollection<TagViewModel> _selectedTags = new ObservableCollection<TagViewModel>();

	private bool _isRelatedImagesMode;

	private MediaFile? _relatedAnchorPhoto;

	private bool _relatedTimeEnabled = true;

	private bool _relatedLocationEnabled;

	private int _relatedTimeValue = UserPreferences.Current.RelatedTimeValue;

	private int _relatedTimeUnitIndex = UserPreferences.Current.RelatedTimeUnitIndex;

	private int _relatedDistanceStepIndex = UserPreferences.Current.RelatedDistanceStepIndex;

	private readonly HashSet<Guid> _relatedPhotoIds = new HashSet<Guid>();

	private CancellationTokenSource _relatedRefreshCts = new CancellationTokenSource();

	private Guid? _similaritySourceId;

	private static readonly int[] _distStepsMeters = new int[6] { 100, 500, 1000, 5000, 10000, 50000 };

	private static readonly string[] _distMetricLabels = new string[6] { "100 m", "500 m", "1 km", "5 km", "10 km", "50 km" };

	private static readonly string[] _distImperialLabels = new string[6] { "330 ft", "0.3 mi", "0.6 mi", "3 mi", "6 mi", "31 mi" };

	private CancellationTokenSource _similarityRefreshCts = new CancellationTokenSource();

	private bool _isFindingRelated;

	private CancellationTokenSource? _relatedCts;

	private bool _isAiSetupRunning;

	private bool _isAiSetupError;

	private string _aiSetupMessage = "";

	private double _aiSetupProgress = -1.0;

	private CancellationTokenSource? _aiSetupCts;

	private Action? _retryAiSetup;

	private string _newTagText = "";

	private GalleryView _activeView;

	private string _searchQuery = "";

	private string _pendingSearchQuery = "";

	private CancellationTokenSource _loadCts = new CancellationTokenSource();

	private double _thumbnailSize = 180.0;

	private bool _isLoading;

	private bool _isSemanticSearch;

	private bool _isSimilaritySearch;

	private bool _isFtsRefreshing;

	private string _similaritySourceName = "";

	private int _similarityCount = 25;

	private int _offlineCount;

	private int _pendingDescriptionCount;

	private bool _allMetadataLoaded;

	private Guid? _activeAlbumId;

	private string _activeAlbumName = "";

	private TaskbarItemProgressState _taskbarProgressState;

	private double _taskbarProgressValue;

	private MediaFile? _selectionAnchor;

	private bool _isPersonFilterActive;

	private IReadOnlyList<Guid> _personFilterPhotoIds = Array.Empty<Guid>();

	private string _activeFilterLabel = "";

	private string _displayDescription = "";

	private bool _hasDisplayDescription;

	private bool _hasUserDescription;

	private string _descriptionLabel = "AI DESCRIPTION";

	private bool _isEditingDescription;

	private string _editingDescriptionText = "";

	private string _descriptionModelLine = "";

	private bool _hasDescriptionModelLine;

	private string _displayCaption = "";

	private bool _hasDisplayCaption;

	private bool _isEditingCaption;

	private string _editingCaptionText = "";

	private bool _isDescriptionCurrent;

	private bool _isDescriptionStale;

	private string _descriptionFreshnessTooltip = "";

	private int _outdatedDescriptionCount;

	private List<(GallerySortField Field, bool Descending)> _sortHistory = new List<(GallerySortField, bool)>();

	private readonly ConcurrentQueue<Guid> _visionQueueNormal = new ConcurrentQueue<Guid>();

	private readonly ConcurrentQueue<Guid> _visionQueuePriority = new ConcurrentQueue<Guid>();

	private readonly SemaphoreSlim _visionSignal = new SemaphoreSlim(0);

	private readonly ConcurrentDictionary<Guid, byte> _visionSkipIds = new ConcurrentDictionary<Guid, byte>();

	private readonly CancellationTokenSource _visionWorkerCts = new CancellationTokenSource();

	private readonly CancellationTokenSource _faceWorkerCts = new CancellationTokenSource();

	private readonly SemaphoreSlim _analysisLock = new SemaphoreSlim(1, 1);

	private DispatcherTimer? _healTimer;

	private EventHandler? _healTimerTick;

	private bool _isAddTagPopupOpen;

	private string _batchTagText = "";

	private List<string> _allTagNames = new List<string>();

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? retryAiSetupCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? cancelRelatedSearchCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? findRelatedImagesCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? findSimilarCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? exitRelatedImagesModeCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? saveRelatedAsAlbumCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? refreshRelatedImagesCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? clearPersonFilterCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? openInMapsCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? sortByDateTakenCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? sortByFileNameCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? sortByFileSizeCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? sortByDateImportedCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? sortByCameraCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? showAllPhotosCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? showFavoritesCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? showOnThisDayCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand<MediaFile>? showOnThisDayPhotoCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand<OnThisDayGroup?>? saveOnThisDayGroupAsAlbumCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? showTagsCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand<LibraryNodeViewModel?>? navigateToAlbumSidebarCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? showPeopleCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? manageLibrariesCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? removeFromAlbumCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? addToAlbumCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? importCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand<(string folder, bool recursive)>? importFolderFromShellCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand<(string folder, bool recursive)>? removeFolderFromShellCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? importFilesCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? beginEditDescriptionCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? saveDescriptionCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? cancelEditDescriptionCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? beginEditCaptionCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? saveCaptionCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? cancelEditCaptionCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? reanalyzeSelectedCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? reanalyzeAllCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand<MediaFile?>? toggleSelectionCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand<MediaFile?>? selectRangeCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? selectAllCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? clearSelectionCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? executeSearchCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand<string>? searchByTagCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? clearSearchCommand;
	private RelayCommand? cancelSearchCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? reanalyzeMultiSelectedCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? reanalyzeOutdatedCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? navigatePreviousCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? navigateNextCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand<int>? navigateByOffsetCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? openInExplorerCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? copyFullPathCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? copyToFolderCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? sendToEmailCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? sendToPrintCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? setAsWallpaperCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? sendToOpenWithCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand<string>? openInExternalEditorCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? excludeFolderCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? includeFolderCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? excludeImageCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? removeFromLibraryCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? deletePermanentlyCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? regenerateThumbnailsCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? openPhotoViewerCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? openSettingsCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand<IReadOnlyList<Guid>?>? openBackupWindowCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand<LibraryNodeViewModel?>? openBackupWindowForAlbumCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? scanDrivesCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? findDuplicatesCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? rotateClockwiseCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? rotateCounterClockwiseCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? toggleFavoriteCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand<TagViewModel>? removeTagCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? loadAllMetadataCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? showAddTagPopupCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? confirmBatchTagCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand? closeAddTagPopupCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private RelayCommand<string>? selectTagSuggestionCommand;

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	private AsyncRelayCommand? addTagCommand;

	public MetricsViewModel Metrics { get; }

	public OnThisDayViewModel OnThisDay { get; } = new OnThisDayViewModel();

	public string PhotoCountText
	{
		get
		{
			if (IsSearchActive)
			{
				return $"{PhotoCount:N0} of {TotalLibraryCount:N0} photos";
			}
			return $"{PhotoCount:N0} photos";
		}
	}

	public string PhotoCountTooltip => IsSearchActive
		? $"{PhotoCount:N0} photos match the current filter · {TotalLibraryCount:N0} total in library"
		: $"{TotalLibraryCount:N0} photos in library";

	public bool ShowExpressLimit => UserPreferences.Current.IsExpressMode;

	public string ExpressLimitText => $"Express  ·  {TotalLibraryCount:N0} / {25000:N0}";

	public IReadOnlyList<string> RelatedTimeUnitNames { get; } = new string[5] { "Minutes", "Hours", "Days", "Weeks", "Months" };

	public string RelatedDistanceLabel
	{
		get
		{
			string[] array = (UserPreferences.Current.UseImperialUnits ? _distImperialLabels : _distMetricLabels);
			return array[Math.Clamp(RelatedDistanceStepIndex, 0, array.Length - 1)];
		}
	}

	public bool HasPendingDescriptions => PendingDescriptionCount > 0;

	public string PendingDescriptionText => $"{PendingDescriptionCount} awaiting AI";

	public ObservableCollection<MetadataTag> AllMetadata { get; } = new ObservableCollection<MetadataTag>();

	public ObservableCollection<LibraryNodeViewModel> SidebarLibraries { get; } = new ObservableCollection<LibraryNodeViewModel>();

	public bool HasOfflineFiles => OfflineCount > 0;

	public string OfflineStatusText => $"{OfflineCount} photo{((OfflineCount == 1) ? "" : "s")} on offline drives";

	public bool SelectedIsOffline
	{
		get
		{
			MediaFile? selectedMediaFile = SelectedMediaFile;
			if (selectedMediaFile == null)
			{
				return false;
			}
			return selectedMediaFile.IsOffline;
		}
	}

	public bool SelectedFolderIsExcluded
	{
		get
		{
			MediaFile? selectedMediaFile = SelectedMediaFile;
			string text = ((((selectedMediaFile != null) ? selectedMediaFile.FilePath : null) == null) ? null : Path.GetDirectoryName(SelectedMediaFile.FilePath));
			if (text == null)
			{
				return false;
			}
			if (_excludedFullPaths.Contains(text))
			{
				return true;
			}
			string fileName = Path.GetFileName(text);
			if (!string.IsNullOrEmpty(fileName))
			{
				return _excludedNames.Contains(fileName);
			}
			return false;
		}
	}

	public ObservableCollection<MediaFile> MultiSelectedItems { get; } = new ObservableCollection<MediaFile>();

	public int MultiSelectedVersion { get; private set; }

	public int SelectedCount => MultiSelectedItems.Count;

	public bool HasAnyMultiSelected => MultiSelectedItems.Count > 0;

	public bool HasMultiSelection => MultiSelectedItems.Count > 1;

	public string ReanalyzeMultiSelectedLabel => $"Re-analyze selected ({SelectedCount})";

	public string ExcludeImageLabel
	{
		get
		{
			if (!HasMultiSelection)
			{
				return "Exclude this Image";
			}
			return $"Exclude {SelectedCount} Images";
		}
	}

	public string RemoveFromLibraryLabel
	{
		get
		{
			if (!HasMultiSelection)
			{
				return "Remove from Library";
			}
			return $"Remove {SelectedCount} from Library";
		}
	}

	public string DeletePermanentlyLabel
	{
		get
		{
			if (!HasMultiSelection)
			{
				return "Delete Permanently";
			}
			return $"Delete {SelectedCount} Photos Permanently";
		}
	}

	public bool HasAnySelected
	{
		get
		{
			if (!HasAnyMultiSelected)
			{
				return SelectedMediaFile != null;
			}
			return true;
		}
	}

	public bool IsReanalyzeSelectedEnabled => !SelectedIsOffline;

	public bool IsLibraryEmpty
	{
		get
		{
			if (PhotoCount == 0 && string.IsNullOrWhiteSpace(SearchQuery) && !IsRelatedImagesMode && !IsPersonFilterActive)
			{
				return !IsSimilaritySearch;
			}
			return false;
		}
	}

	public bool HasNoResults
	{
		get
		{
			if (PhotoCount == 0 && !string.IsNullOrWhiteSpace(SearchQuery) && !IsRelatedImagesMode && !IsPersonFilterActive)
			{
				return !IsSimilaritySearch;
			}
			return false;
		}
	}

	public bool IsRelatedNoResults
	{
		get
		{
			if (IsRelatedImagesMode)
			{
				return PhotoCount == 0;
			}
			return false;
		}
	}

	public bool IsSearchActive => !string.IsNullOrWhiteSpace(SearchQuery);

	public bool IsSemanticAvailable => _semanticSearch.IsModelAvailable;

	public bool ShowSemanticUnavailableHint
	{
		get
		{
			if (IsSearchActive)
			{
				return !IsSemanticAvailable;
			}
			return false;
		}
	}

	public bool ShowSearchProgress
	{
		get
		{
			if (IsLoading)
			{
				return IsSearchActive;
			}
			return false;
		}
	}

	public string LoadingStatusText
	{
		get
		{
			if (!IsSimilaritySearch)
			{
				if (!IsSearchActive)
				{
					return "Loading…";
				}
				return "Searching…";
			}
			return "Finding photos similar to " + SimilaritySourceName + "…";
		}
	}

	public bool SelectedHasEmbedding
	{
		get
		{
			MediaFile? selectedMediaFile = SelectedMediaFile;
			if (((selectedMediaFile != null) ? selectedMediaFile.ClipEmbeddingBytes : null) != null)
			{
				return IsSemanticAvailable;
			}
			return false;
		}
	}

	public string NoResultsMessage => "No photos found for \"" + SearchQuery + "\"";

	public bool IsEmpty => PhotoCount == 0;

	public bool HasSelection => SelectedMediaFile != null;

	public bool HasAiDescription
	{
		get
		{
			MediaFile? selectedMediaFile = SelectedMediaFile;
			return !string.IsNullOrEmpty((selectedMediaFile != null) ? selectedMediaFile.AiDescription : null);
		}
	}

	public bool DescriptionAttemptedButFailed
	{
		get
		{
			MediaFile? selectedMediaFile = SelectedMediaFile;
			if (((selectedMediaFile != null) ? selectedMediaFile.AiDescription : null) == string.Empty)
			{
				MediaFile? selectedMediaFile2 = SelectedMediaFile;
				return ((selectedMediaFile2 != null) ? selectedMediaFile2.UserDescription : null) == null;
			}
			return false;
		}
	}

	public bool HasSelectedTags => SelectedTags.Count > 0;

	public bool HasOutdatedDescriptions => OutdatedDescriptionCount > 0;

	public string SelectedDateText
	{
		get
		{
			MediaFile? selectedMediaFile = SelectedMediaFile;
			return ((selectedMediaFile == null) ? null : selectedMediaFile.DateTaken?.ToString("MMMM d, yyyy")) ?? "";
		}
	}

	public string SelectedTimeText
	{
		get
		{
			MediaFile? selectedMediaFile = SelectedMediaFile;
			return ((selectedMediaFile == null) ? null : selectedMediaFile.DateTaken?.ToString("h:mm tt")) ?? "";
		}
	}

	public string SelectedDimensionsText
	{
		get
		{
			MediaFile selectedMediaFile = SelectedMediaFile;
			if (selectedMediaFile == null || selectedMediaFile.Width <= 0 || selectedMediaFile.Height <= 0)
			{
				return "";
			}
			return $"{selectedMediaFile.Width} × {selectedMediaFile.Height}";
		}
	}

	public string SelectedFileSizeText
	{
		get
		{
			if (SelectedMediaFile == null)
			{
				return "";
			}
			return FormatUtilities.FormatFileSize(SelectedMediaFile.FileSize);
		}
	}

	public bool SelectedIsFavorite
	{
		get
		{
			MediaFile? selectedMediaFile = SelectedMediaFile;
			if (selectedMediaFile == null)
			{
				return false;
			}
			return selectedMediaFile.IsFavorite;
		}
	}

	public string SelectedApertureText
	{
		get
		{
			MediaFile? selectedMediaFile = SelectedMediaFile;
			double? num = ((selectedMediaFile != null) ? selectedMediaFile.Aperture : ((double?)null));
			if (num.HasValue)
			{
				double valueOrDefault = num.GetValueOrDefault();
				return $"f/{valueOrDefault:F1}";
			}
			return "";
		}
	}

	public string SelectedShutterText
	{
		get
		{
			MediaFile? selectedMediaFile = SelectedMediaFile;
			string text = ((selectedMediaFile != null) ? selectedMediaFile.ShutterSpeed : null);
			if (text == null)
			{
				return "";
			}
			return text + " s";
		}
	}

	public string SelectedIsoText
	{
		get
		{
			MediaFile? selectedMediaFile = SelectedMediaFile;
			int? num = ((selectedMediaFile != null) ? selectedMediaFile.ISO : ((int?)null));
			if (num.HasValue)
			{
				int valueOrDefault = num.GetValueOrDefault();
				return $"ISO {valueOrDefault}";
			}
			return "";
		}
	}

	public string SelectedFocalLengthText
	{
		get
		{
			MediaFile? selectedMediaFile = SelectedMediaFile;
			double? num = ((selectedMediaFile != null) ? selectedMediaFile.FocalLength : ((double?)null));
			if (num.HasValue)
			{
				double valueOrDefault = num.GetValueOrDefault();
				return $"{valueOrDefault:F0} mm";
			}
			return "";
		}
	}

	public bool HasAperture
	{
		get
		{
			MediaFile? selectedMediaFile = SelectedMediaFile;
			if (selectedMediaFile == null)
			{
				return false;
			}
			return selectedMediaFile.Aperture.HasValue;
		}
	}

	public bool HasShutterSpeed
	{
		get
		{
			MediaFile? selectedMediaFile = SelectedMediaFile;
			return ((selectedMediaFile != null) ? selectedMediaFile.ShutterSpeed : null) != null;
		}
	}

	public bool HasIso
	{
		get
		{
			MediaFile? selectedMediaFile = SelectedMediaFile;
			if (selectedMediaFile == null)
			{
				return false;
			}
			return selectedMediaFile.ISO.HasValue;
		}
	}

	public bool HasFocalLength
	{
		get
		{
			MediaFile? selectedMediaFile = SelectedMediaFile;
			if (selectedMediaFile == null)
			{
				return false;
			}
			return selectedMediaFile.FocalLength.HasValue;
		}
	}

	public bool HasExifData
	{
		get
		{
			if (!HasAperture && !HasShutterSpeed && !HasIso)
			{
				return HasFocalLength;
			}
			return true;
		}
	}

	public bool HasGps
	{
		get
		{
			MediaFile? selectedMediaFile = SelectedMediaFile;
			if (selectedMediaFile != null && selectedMediaFile.Latitude.HasValue)
			{
				MediaFile? selectedMediaFile2 = SelectedMediaFile;
				if (selectedMediaFile2 == null)
				{
					return false;
				}
				return selectedMediaFile2.Longitude.HasValue;
			}
			return false;
		}
	}

	public string SelectedGpsText
	{
		get
		{
			MediaFile selectedMediaFile = SelectedMediaFile;
			if (selectedMediaFile != null)
			{
				double? latitude = selectedMediaFile.Latitude;
				if (latitude.HasValue)
				{
					double valueOrDefault = latitude.GetValueOrDefault();
					double? longitude = selectedMediaFile.Longitude;
					if (longitude.HasValue)
					{
						double valueOrDefault2 = longitude.GetValueOrDefault();
						string value = ((valueOrDefault >= 0.0) ? "N" : "S");
						string value2 = ((valueOrDefault2 >= 0.0) ? "E" : "W");
						return $"{Math.Abs(valueOrDefault):F4}° {value},  {Math.Abs(valueOrDefault2):F4}° {value2}";
					}
				}
			}
			return "";
		}
	}

	public string ActiveVisionModel
	{
		get
		{
			if (!UserPreferences.Current.IsExpressMode)
			{
				return UserPreferences.Current.VisionModelName;
			}
			return "Express";
		}
	}

	public bool IsAllPhotosActive => ActiveView == GalleryView.AllPhotos;

	public bool IsFavoritesActive => ActiveView == GalleryView.Favorites;

	public bool IsAlbumActive => ActiveView == GalleryView.Album;

	public bool IsPeopleActive => ActiveView == GalleryView.People;

	public bool IsOnThisDayActive => ActiveView == GalleryView.OnThisDay;

	public bool IsGalleryVisible => ActiveView != GalleryView.OnThisDay;

	public string DateTakenSortLabel => GetSortLabel(GallerySortField.DateTaken);

	public string FileNameSortLabel => GetSortLabel(GallerySortField.FileName);

	public string FileSizeSortLabel => GetSortLabel(GallerySortField.FileSize);

	public string DateImportedSortLabel => GetSortLabel(GallerySortField.DateImported);

	public string CameraSortLabel => GetSortLabel(GallerySortField.Camera);

	public bool DateTakenSortActive => IsSortActive(GallerySortField.DateTaken);

	public bool FileNameSortActive => IsSortActive(GallerySortField.FileName);

	public bool FileSizeSortActive => IsSortActive(GallerySortField.FileSize);

	public bool DateImportedSortActive => IsSortActive(GallerySortField.DateImported);

	public bool CameraSortActive => IsSortActive(GallerySortField.Camera);

	public ImportProgressViewModel Progress { get; }

	public ObservableCollection<SendToMenuItem> SendToMenuItems { get; } = new ObservableCollection<SendToMenuItem>();

	public ObservableCollection<string> TagSuggestions { get; } = new ObservableCollection<string>();

	public string AddTagPopupTitle
	{
		get
		{
			if (!HasMultiSelection)
			{
				return "Add tag to photo";
			}
			return $"Add tag to {SelectedCount} photos";
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public ObservableCollection<MediaFile> MediaFiles
	{
		get
		{
			return _mediaFiles;
		}
		[MemberNotNull("_mediaFiles")]
		set
		{
			if (!EqualityComparer<ObservableCollection<MediaFile>>.Default.Equals(_mediaFiles, value))
			{
				OnPropertyChanging("MediaFiles");
				_mediaFiles = value;
				OnPropertyChanged("MediaFiles");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public MediaFile? SelectedMediaFile
	{
		get
		{
			return _selectedMediaFile;
		}
		set
		{
			if (!EqualityComparer<MediaFile>.Default.Equals(_selectedMediaFile, value))
			{
				OnPropertyChanging("SelectedMediaFile");
				_selectedMediaFile = value;
				OnSelectedMediaFileChanged(value);
				OnPropertyChanged("SelectedMediaFile");
				((IRelayCommand)FindRelatedImagesCommand).NotifyCanExecuteChanged();
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string StatusText
	{
		get
		{
			return _statusText;
		}
		[MemberNotNull("_statusText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_statusText, value))
			{
				OnPropertyChanging("StatusText");
				_statusText = value;
				OnPropertyChanged("StatusText");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public int PhotoCount
	{
		get
		{
			return _photoCount;
		}
		set
		{
			if (!EqualityComparer<int>.Default.Equals(_photoCount, value))
			{
				OnPropertyChanging("PhotoCount");
				_photoCount = value;
				OnPropertyChanged("PhotoCount");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public int TotalLibraryCount
	{
		get
		{
			return _totalLibraryCount;
		}
		set
		{
			if (!EqualityComparer<int>.Default.Equals(_totalLibraryCount, value))
			{
				OnPropertyChanging("TotalLibraryCount");
				_totalLibraryCount = value;
				OnPropertyChanged("TotalLibraryCount");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public ObservableCollection<TagViewModel> SelectedTags
	{
		get
		{
			return _selectedTags;
		}
		[MemberNotNull("_selectedTags")]
		set
		{
			if (!EqualityComparer<ObservableCollection<TagViewModel>>.Default.Equals(_selectedTags, value))
			{
				OnPropertyChanging("SelectedTags");
				_selectedTags = value;
				OnPropertyChanged("SelectedTags");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsRelatedImagesMode
	{
		get
		{
			return _isRelatedImagesMode;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isRelatedImagesMode, value))
			{
				OnPropertyChanging("IsRelatedImagesMode");
				OnPropertyChanging("IsLibraryEmpty");
				OnPropertyChanging("HasNoResults");
				OnPropertyChanging("IsRelatedNoResults");
				_isRelatedImagesMode = value;
				OnPropertyChanged("IsRelatedImagesMode");
				OnPropertyChanged("IsLibraryEmpty");
				OnPropertyChanged("HasNoResults");
				OnPropertyChanged("IsRelatedNoResults");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public MediaFile? RelatedAnchorPhoto
	{
		get
		{
			return _relatedAnchorPhoto;
		}
		set
		{
			if (!EqualityComparer<MediaFile>.Default.Equals(_relatedAnchorPhoto, value))
			{
				OnPropertyChanging("RelatedAnchorPhoto");
				_relatedAnchorPhoto = value;
				OnPropertyChanged("RelatedAnchorPhoto");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool RelatedTimeEnabled
	{
		get
		{
			return _relatedTimeEnabled;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_relatedTimeEnabled, value))
			{
				OnPropertyChanging("RelatedTimeEnabled");
				_relatedTimeEnabled = value;
				OnPropertyChanged("RelatedTimeEnabled");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool RelatedLocationEnabled
	{
		get
		{
			return _relatedLocationEnabled;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_relatedLocationEnabled, value))
			{
				OnPropertyChanging("RelatedLocationEnabled");
				_relatedLocationEnabled = value;
				OnPropertyChanged("RelatedLocationEnabled");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public int RelatedTimeValue
	{
		get
		{
			return _relatedTimeValue;
		}
		set
		{
			if (!EqualityComparer<int>.Default.Equals(_relatedTimeValue, value))
			{
				OnPropertyChanging("RelatedTimeValue");
				_relatedTimeValue = value;
				OnRelatedTimeValueChanged(value);
				OnPropertyChanged("RelatedTimeValue");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public int RelatedTimeUnitIndex
	{
		get
		{
			return _relatedTimeUnitIndex;
		}
		set
		{
			if (!EqualityComparer<int>.Default.Equals(_relatedTimeUnitIndex, value))
			{
				OnPropertyChanging("RelatedTimeUnitIndex");
				_relatedTimeUnitIndex = value;
				OnRelatedTimeUnitIndexChanged(value);
				OnPropertyChanged("RelatedTimeUnitIndex");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public int RelatedDistanceStepIndex
	{
		get
		{
			return _relatedDistanceStepIndex;
		}
		set
		{
			if (!EqualityComparer<int>.Default.Equals(_relatedDistanceStepIndex, value))
			{
				OnPropertyChanging("RelatedDistanceStepIndex");
				OnPropertyChanging("RelatedDistanceLabel");
				_relatedDistanceStepIndex = value;
				OnRelatedDistanceStepIndexChanged(value);
				OnPropertyChanged("RelatedDistanceStepIndex");
				OnPropertyChanged("RelatedDistanceLabel");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsFindingRelated
	{
		get
		{
			return _isFindingRelated;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isFindingRelated, value))
			{
				OnPropertyChanging("IsFindingRelated");
				_isFindingRelated = value;
				OnPropertyChanged("IsFindingRelated");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsAiSetupRunning
	{
		get
		{
			return _isAiSetupRunning;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isAiSetupRunning, value))
			{
				OnPropertyChanging("IsAiSetupRunning");
				_isAiSetupRunning = value;
				OnPropertyChanged("IsAiSetupRunning");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsAiSetupError
	{
		get
		{
			return _isAiSetupError;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isAiSetupError, value))
			{
				OnPropertyChanging("IsAiSetupError");
				_isAiSetupError = value;
				OnPropertyChanged("IsAiSetupError");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string AiSetupMessage
	{
		get
		{
			return _aiSetupMessage;
		}
		[MemberNotNull("_aiSetupMessage")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_aiSetupMessage, value))
			{
				OnPropertyChanging("AiSetupMessage");
				_aiSetupMessage = value;
				OnPropertyChanged("AiSetupMessage");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public double AiSetupProgress
	{
		get
		{
			return _aiSetupProgress;
		}
		set
		{
			if (!EqualityComparer<double>.Default.Equals(_aiSetupProgress, value))
			{
				OnPropertyChanging("AiSetupProgress");
				_aiSetupProgress = value;
				OnPropertyChanged("AiSetupProgress");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string NewTagText
	{
		get
		{
			return _newTagText;
		}
		[MemberNotNull("_newTagText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_newTagText, value))
			{
				OnPropertyChanging("NewTagText");
				_newTagText = value;
				OnPropertyChanged("NewTagText");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public GalleryView ActiveView
	{
		get
		{
			return _activeView;
		}
		set
		{
			if (!EqualityComparer<GalleryView>.Default.Equals(_activeView, value))
			{
				OnPropertyChanging("ActiveView");
				_activeView = value;
				OnActiveViewChanged(value);
				OnPropertyChanged("ActiveView");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string SearchQuery
	{
		get
		{
			return _searchQuery;
		}
		[MemberNotNull("_searchQuery")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_searchQuery, value))
			{
				OnPropertyChanging("SearchQuery");
				_searchQuery = value;
				OnPropertyChanged("SearchQuery");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string PendingSearchQuery
	{
		get
		{
			return _pendingSearchQuery;
		}
		[MemberNotNull("_pendingSearchQuery")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_pendingSearchQuery, value))
			{
				OnPropertyChanging("PendingSearchQuery");
				_pendingSearchQuery = value;
				OnPropertyChanged("PendingSearchQuery");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public double ThumbnailSize
	{
		get
		{
			return _thumbnailSize;
		}
		set
		{
			if (!EqualityComparer<double>.Default.Equals(_thumbnailSize, value))
			{
				OnPropertyChanging("ThumbnailSize");
				_thumbnailSize = value;
				OnPropertyChanged("ThumbnailSize");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsLoading
	{
		get
		{
			return _isLoading;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isLoading, value))
			{
				OnPropertyChanging("IsLoading");
				OnPropertyChanging("ShowSearchProgress");
				OnPropertyChanging("LoadingStatusText");
				_isLoading = value;
				OnPropertyChanged("IsLoading");
				OnPropertyChanged("ShowSearchProgress");
				OnPropertyChanged("LoadingStatusText");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsSemanticSearch
	{
		get
		{
			return _isSemanticSearch;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isSemanticSearch, value))
			{
				OnPropertyChanging("IsSemanticSearch");
				_isSemanticSearch = value;
				OnPropertyChanged("IsSemanticSearch");
			}
		}
	}

	public bool IsFtsRefreshing
	{
		get => _isFtsRefreshing;
		private set
		{
			if (_isFtsRefreshing != value)
			{
				OnPropertyChanging(nameof(IsFtsRefreshing));
				_isFtsRefreshing = value;
				OnPropertyChanged(nameof(IsFtsRefreshing));
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsSimilaritySearch
	{
		get
		{
			return _isSimilaritySearch;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isSimilaritySearch, value))
			{
				OnPropertyChanging("IsSimilaritySearch");
				OnPropertyChanging("HasNoResults");
				OnPropertyChanging("IsLibraryEmpty");
				_isSimilaritySearch = value;
				OnPropertyChanged("IsSimilaritySearch");
				OnPropertyChanged("HasNoResults");
				OnPropertyChanged("IsLibraryEmpty");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string SimilaritySourceName
	{
		get
		{
			return _similaritySourceName;
		}
		[MemberNotNull("_similaritySourceName")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_similaritySourceName, value))
			{
				OnPropertyChanging("SimilaritySourceName");
				_similaritySourceName = value;
				OnPropertyChanged("SimilaritySourceName");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public int SimilarityCount
	{
		get
		{
			return _similarityCount;
		}
		set
		{
			if (!EqualityComparer<int>.Default.Equals(_similarityCount, value))
			{
				OnPropertyChanging("SimilarityCount");
				_similarityCount = value;
				OnSimilarityCountChanged(value);
				OnPropertyChanged("SimilarityCount");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public int OfflineCount
	{
		get
		{
			return _offlineCount;
		}
		set
		{
			if (!EqualityComparer<int>.Default.Equals(_offlineCount, value))
			{
				OnPropertyChanging("OfflineCount");
				_offlineCount = value;
				OnPropertyChanged("OfflineCount");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public int PendingDescriptionCount
	{
		get
		{
			return _pendingDescriptionCount;
		}
		set
		{
			if (!EqualityComparer<int>.Default.Equals(_pendingDescriptionCount, value))
			{
				OnPropertyChanging("PendingDescriptionCount");
				OnPropertyChanging("HasPendingDescriptions");
				OnPropertyChanging("PendingDescriptionText");
				_pendingDescriptionCount = value;
				OnPropertyChanged("PendingDescriptionCount");
				OnPropertyChanged("HasPendingDescriptions");
				OnPropertyChanged("PendingDescriptionText");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public Guid? ActiveAlbumId
	{
		get
		{
			return _activeAlbumId;
		}
		set
		{
			if (!EqualityComparer<Guid?>.Default.Equals(_activeAlbumId, value))
			{
				OnPropertyChanging("ActiveAlbumId");
				_activeAlbumId = value;
				OnPropertyChanged("ActiveAlbumId");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string ActiveAlbumName
	{
		get
		{
			return _activeAlbumName;
		}
		[MemberNotNull("_activeAlbumName")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_activeAlbumName, value))
			{
				OnPropertyChanging("ActiveAlbumName");
				_activeAlbumName = value;
				OnPropertyChanged("ActiveAlbumName");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public TaskbarItemProgressState TaskbarProgressState
	{
		get
		{
			return _taskbarProgressState;
		}
		set
		{
			if (!EqualityComparer<TaskbarItemProgressState>.Default.Equals(_taskbarProgressState, value))
			{
				OnPropertyChanging("TaskbarProgressState");
				_taskbarProgressState = value;
				OnPropertyChanged("TaskbarProgressState");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public double TaskbarProgressValue
	{
		get
		{
			return _taskbarProgressValue;
		}
		set
		{
			if (!EqualityComparer<double>.Default.Equals(_taskbarProgressValue, value))
			{
				OnPropertyChanging("TaskbarProgressValue");
				_taskbarProgressValue = value;
				OnPropertyChanged("TaskbarProgressValue");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsPersonFilterActive
	{
		get
		{
			return _isPersonFilterActive;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isPersonFilterActive, value))
			{
				OnPropertyChanging("IsPersonFilterActive");
				OnPropertyChanging("IsLibraryEmpty");
				OnPropertyChanging("HasNoResults");
				_isPersonFilterActive = value;
				OnPropertyChanged("IsPersonFilterActive");
				OnPropertyChanged("IsLibraryEmpty");
				OnPropertyChanged("HasNoResults");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string ActiveFilterLabel
	{
		get
		{
			return _activeFilterLabel;
		}
		[MemberNotNull("_activeFilterLabel")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_activeFilterLabel, value))
			{
				OnPropertyChanging("ActiveFilterLabel");
				_activeFilterLabel = value;
				OnPropertyChanged("ActiveFilterLabel");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string DisplayDescription
	{
		get
		{
			return _displayDescription;
		}
		[MemberNotNull("_displayDescription")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_displayDescription, value))
			{
				OnPropertyChanging("DisplayDescription");
				_displayDescription = value;
				OnPropertyChanged("DisplayDescription");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool HasDisplayDescription
	{
		get
		{
			return _hasDisplayDescription;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_hasDisplayDescription, value))
			{
				OnPropertyChanging("HasDisplayDescription");
				_hasDisplayDescription = value;
				OnPropertyChanged("HasDisplayDescription");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool HasUserDescription
	{
		get
		{
			return _hasUserDescription;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_hasUserDescription, value))
			{
				OnPropertyChanging("HasUserDescription");
				_hasUserDescription = value;
				OnPropertyChanged("HasUserDescription");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string DescriptionLabel
	{
		get
		{
			return _descriptionLabel;
		}
		[MemberNotNull("_descriptionLabel")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_descriptionLabel, value))
			{
				OnPropertyChanging("DescriptionLabel");
				_descriptionLabel = value;
				OnPropertyChanged("DescriptionLabel");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsEditingDescription
	{
		get
		{
			return _isEditingDescription;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isEditingDescription, value))
			{
				OnPropertyChanging("IsEditingDescription");
				_isEditingDescription = value;
				OnIsEditingDescriptionChanged(value);
				OnPropertyChanged("IsEditingDescription");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string EditingDescriptionText
	{
		get
		{
			return _editingDescriptionText;
		}
		[MemberNotNull("_editingDescriptionText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_editingDescriptionText, value))
			{
				OnPropertyChanging("EditingDescriptionText");
				_editingDescriptionText = value;
				OnPropertyChanged("EditingDescriptionText");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string DescriptionModelLine
	{
		get
		{
			return _descriptionModelLine;
		}
		[MemberNotNull("_descriptionModelLine")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_descriptionModelLine, value))
			{
				OnPropertyChanging("DescriptionModelLine");
				_descriptionModelLine = value;
				OnPropertyChanged("DescriptionModelLine");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool HasDescriptionModelLine
	{
		get
		{
			return _hasDescriptionModelLine;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_hasDescriptionModelLine, value))
			{
				OnPropertyChanging("HasDescriptionModelLine");
				_hasDescriptionModelLine = value;
				OnPropertyChanged("HasDescriptionModelLine");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string DisplayCaption
	{
		get
		{
			return _displayCaption;
		}
		[MemberNotNull("_displayCaption")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_displayCaption, value))
			{
				OnPropertyChanging("DisplayCaption");
				_displayCaption = value;
				OnPropertyChanged("DisplayCaption");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool HasDisplayCaption
	{
		get
		{
			return _hasDisplayCaption;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_hasDisplayCaption, value))
			{
				OnPropertyChanging("HasDisplayCaption");
				_hasDisplayCaption = value;
				OnPropertyChanged("HasDisplayCaption");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsEditingCaption
	{
		get
		{
			return _isEditingCaption;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isEditingCaption, value))
			{
				OnPropertyChanging("IsEditingCaption");
				_isEditingCaption = value;
				OnIsEditingCaptionChanged(value);
				OnPropertyChanged("IsEditingCaption");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string EditingCaptionText
	{
		get
		{
			return _editingCaptionText;
		}
		[MemberNotNull("_editingCaptionText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_editingCaptionText, value))
			{
				OnPropertyChanging("EditingCaptionText");
				_editingCaptionText = value;
				OnPropertyChanged("EditingCaptionText");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsDescriptionCurrent
	{
		get
		{
			return _isDescriptionCurrent;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isDescriptionCurrent, value))
			{
				OnPropertyChanging("IsDescriptionCurrent");
				_isDescriptionCurrent = value;
				OnPropertyChanged("IsDescriptionCurrent");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsDescriptionStale
	{
		get
		{
			return _isDescriptionStale;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isDescriptionStale, value))
			{
				OnPropertyChanging("IsDescriptionStale");
				_isDescriptionStale = value;
				OnPropertyChanged("IsDescriptionStale");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string DescriptionFreshnessTooltip
	{
		get
		{
			return _descriptionFreshnessTooltip;
		}
		[MemberNotNull("_descriptionFreshnessTooltip")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_descriptionFreshnessTooltip, value))
			{
				OnPropertyChanging("DescriptionFreshnessTooltip");
				_descriptionFreshnessTooltip = value;
				OnPropertyChanged("DescriptionFreshnessTooltip");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public int OutdatedDescriptionCount
	{
		get
		{
			return _outdatedDescriptionCount;
		}
		set
		{
			if (!EqualityComparer<int>.Default.Equals(_outdatedDescriptionCount, value))
			{
				OnPropertyChanging("OutdatedDescriptionCount");
				OnPropertyChanging("HasOutdatedDescriptions");
				_outdatedDescriptionCount = value;
				OnPropertyChanged("OutdatedDescriptionCount");
				OnPropertyChanged("HasOutdatedDescriptions");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public bool IsAddTagPopupOpen
	{
		get
		{
			return _isAddTagPopupOpen;
		}
		set
		{
			if (!EqualityComparer<bool>.Default.Equals(_isAddTagPopupOpen, value))
			{
				OnPropertyChanging("IsAddTagPopupOpen");
				_isAddTagPopupOpen = value;
				OnPropertyChanged("IsAddTagPopupOpen");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public string BatchTagText
	{
		get
		{
			return _batchTagText;
		}
		[MemberNotNull("_batchTagText")]
		set
		{
			if (!EqualityComparer<string>.Default.Equals(_batchTagText, value))
			{
				OnPropertyChanging("BatchTagText");
				_batchTagText = value;
				OnBatchTagTextChanged(value);
				OnPropertyChanged("BatchTagText");
			}
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand RetryAiSetupCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = retryAiSetupCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)RetryAiSetup);
				RelayCommand val2 = val;
				retryAiSetupCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand CancelRelatedSearchCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = cancelRelatedSearchCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)CancelRelatedSearch);
				RelayCommand val2 = val;
				cancelRelatedSearchCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand FindRelatedImagesCommand
	{
		get
		{
			//IL_0023: Unknown result type (might be due to invalid IL or missing references)
			//IL_0028: Unknown result type (might be due to invalid IL or missing references)
			//IL_002a: Expected O, but got Unknown
			//IL_002f: Expected O, but got Unknown
			AsyncRelayCommand obj = findRelatedImagesCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)FindRelatedImagesAsync, (Func<bool>)CanFindRelatedImages);
				AsyncRelayCommand val2 = val;
				findRelatedImagesCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand FindSimilarCommand
	{
		get
		{
			//IL_0023: Unknown result type (might be due to invalid IL or missing references)
			//IL_0028: Unknown result type (might be due to invalid IL or missing references)
			//IL_002a: Expected O, but got Unknown
			//IL_002f: Expected O, but got Unknown
			AsyncRelayCommand obj = findSimilarCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)FindSimilarAsync, (Func<bool>)CanFindSimilar);
				AsyncRelayCommand val2 = val;
				findSimilarCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ExitRelatedImagesModeCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = exitRelatedImagesModeCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)ExitRelatedImagesModeAsync);
				AsyncRelayCommand val2 = val;
				exitRelatedImagesModeCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand SaveRelatedAsAlbumCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = saveRelatedAsAlbumCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)SaveRelatedAsAlbumAsync);
				AsyncRelayCommand val2 = val;
				saveRelatedAsAlbumCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand RefreshRelatedImagesCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = refreshRelatedImagesCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)RefreshRelatedImagesAsync);
				AsyncRelayCommand val2 = val;
				refreshRelatedImagesCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ClearPersonFilterCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = clearPersonFilterCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)ClearPersonFilterAsync);
				AsyncRelayCommand val2 = val;
				clearPersonFilterCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand OpenInMapsCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = openInMapsCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)OpenInMaps);
				RelayCommand val2 = val;
				openInMapsCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand SortByDateTakenCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = sortByDateTakenCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)SortByDateTaken);
				RelayCommand val2 = val;
				sortByDateTakenCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand SortByFileNameCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = sortByFileNameCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)SortByFileName);
				RelayCommand val2 = val;
				sortByFileNameCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand SortByFileSizeCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = sortByFileSizeCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)SortByFileSize);
				RelayCommand val2 = val;
				sortByFileSizeCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand SortByDateImportedCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = sortByDateImportedCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)SortByDateImported);
				RelayCommand val2 = val;
				sortByDateImportedCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand SortByCameraCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = sortByCameraCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)SortByCamera);
				RelayCommand val2 = val;
				sortByCameraCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand ShowAllPhotosCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = showAllPhotosCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)ShowAllPhotos);
				RelayCommand val2 = val;
				showAllPhotosCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand ShowFavoritesCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = showFavoritesCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)ShowFavorites);
				RelayCommand val2 = val;
				showFavoritesCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ShowOnThisDayCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = showOnThisDayCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)ShowOnThisDayAsync);
				AsyncRelayCommand val2 = val;
				showOnThisDayCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand<MediaFile> ShowOnThisDayPhotoCommand => (IRelayCommand<MediaFile>)(object)(showOnThisDayPhotoCommand ?? (showOnThisDayPhotoCommand = new RelayCommand<MediaFile>((Action<MediaFile>)ShowOnThisDayPhoto)));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand<OnThisDayGroup?> SaveOnThisDayGroupAsAlbumCommand => (IAsyncRelayCommand<OnThisDayGroup?>)(object)(saveOnThisDayGroupAsAlbumCommand ?? (saveOnThisDayGroupAsAlbumCommand = new AsyncRelayCommand<OnThisDayGroup>((Func<OnThisDayGroup, Task>)SaveOnThisDayGroupAsAlbumAsync)));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand ShowTagsCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = showTagsCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)ShowTags);
				RelayCommand val2 = val;
				showTagsCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand<LibraryNodeViewModel?> NavigateToAlbumSidebarCommand => (IRelayCommand<LibraryNodeViewModel?>)(object)(navigateToAlbumSidebarCommand ?? (navigateToAlbumSidebarCommand = new RelayCommand<LibraryNodeViewModel>((Action<LibraryNodeViewModel>)NavigateToAlbumSidebar)));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand ShowPeopleCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = showPeopleCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)ShowPeople);
				RelayCommand val2 = val;
				showPeopleCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ManageLibrariesCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = manageLibrariesCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)ManageLibrariesAsync);
				AsyncRelayCommand val2 = val;
				manageLibrariesCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand RemoveFromAlbumCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = removeFromAlbumCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)RemoveFromAlbumAsync);
				AsyncRelayCommand val2 = val;
				removeFromAlbumCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand AddToAlbumCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = addToAlbumCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)AddToAlbumAsync);
				AsyncRelayCommand val2 = val;
				addToAlbumCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ImportCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = importCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)ImportAsync);
				AsyncRelayCommand val2 = val;
				importCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand<(string folder, bool recursive)> ImportFolderFromShellCommand => (IAsyncRelayCommand<(string folder, bool recursive)>)(object)(importFolderFromShellCommand ?? (importFolderFromShellCommand = new AsyncRelayCommand<(string, bool)>((Func<(string, bool), Task>)ImportFolderFromShellAsync)));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand<(string folder, bool recursive)> RemoveFolderFromShellCommand => (IAsyncRelayCommand<(string folder, bool recursive)>)(object)(removeFolderFromShellCommand ?? (removeFolderFromShellCommand = new AsyncRelayCommand<(string, bool)>((Func<(string, bool), Task>)RemoveFolderFromShellAsync)));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ImportFilesCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = importFilesCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)ImportFilesAsync);
				AsyncRelayCommand val2 = val;
				importFilesCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand BeginEditDescriptionCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = beginEditDescriptionCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)BeginEditDescription);
				RelayCommand val2 = val;
				beginEditDescriptionCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand SaveDescriptionCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = saveDescriptionCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)SaveDescriptionAsync);
				AsyncRelayCommand val2 = val;
				saveDescriptionCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand CancelEditDescriptionCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = cancelEditDescriptionCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)CancelEditDescription);
				RelayCommand val2 = val;
				cancelEditDescriptionCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand BeginEditCaptionCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = beginEditCaptionCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)BeginEditCaption);
				RelayCommand val2 = val;
				beginEditCaptionCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand SaveCaptionCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = saveCaptionCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)SaveCaptionAsync);
				AsyncRelayCommand val2 = val;
				saveCaptionCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand CancelEditCaptionCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = cancelEditCaptionCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)CancelEditCaption);
				RelayCommand val2 = val;
				cancelEditCaptionCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ReanalyzeSelectedCommand
	{
		get
		{
			//IL_0023: Unknown result type (might be due to invalid IL or missing references)
			//IL_0028: Unknown result type (might be due to invalid IL or missing references)
			//IL_002a: Expected O, but got Unknown
			//IL_002f: Expected O, but got Unknown
			AsyncRelayCommand obj = reanalyzeSelectedCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)ReanalyzeSelectedAsync, (Func<bool>)CanRunReanalyze);
				AsyncRelayCommand val2 = val;
				reanalyzeSelectedCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ReanalyzeAllCommand
	{
		get
		{
			//IL_0023: Unknown result type (might be due to invalid IL or missing references)
			//IL_0028: Unknown result type (might be due to invalid IL or missing references)
			//IL_002a: Expected O, but got Unknown
			//IL_002f: Expected O, but got Unknown
			AsyncRelayCommand obj = reanalyzeAllCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)ReanalyzeAllAsync, (Func<bool>)CanRunReanalyze);
				AsyncRelayCommand val2 = val;
				reanalyzeAllCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand<MediaFile?> ToggleSelectionCommand => (IRelayCommand<MediaFile?>)(object)(toggleSelectionCommand ?? (toggleSelectionCommand = new RelayCommand<MediaFile>((Action<MediaFile>)ToggleSelection)));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand<MediaFile?> SelectRangeCommand => (IRelayCommand<MediaFile?>)(object)(selectRangeCommand ?? (selectRangeCommand = new RelayCommand<MediaFile>((Action<MediaFile>)SelectRange)));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand SelectAllCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = selectAllCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)SelectAll);
				RelayCommand val2 = val;
				selectAllCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand ClearSelectionCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = clearSelectionCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)ClearSelection);
				RelayCommand val2 = val;
				clearSelectionCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ExecuteSearchCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = executeSearchCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)ExecuteSearchAsync);
				AsyncRelayCommand val2 = val;
				executeSearchCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand<string> SearchByTagCommand => (IAsyncRelayCommand<string>)(object)(searchByTagCommand ?? (searchByTagCommand = new AsyncRelayCommand<string>((Func<string, Task>)SearchByTagAsync)));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ClearSearchCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = clearSearchCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)ClearSearchAsync);
				AsyncRelayCommand val2 = val;
				clearSearchCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	/// <summary>
	/// Cancels an in-progress search and clears results. Bound to the Cancel button
	/// that replaces the Search button while IsLoading &amp;&amp; IsSearchActive.
	/// </summary>
	public ICommand CancelSearchCommand => cancelSearchCommand ??= new RelayCommand(() =>
	{
		try { _loadCts.Cancel(); } catch { }
		PendingSearchQuery = "";
		SearchQuery = "";
		_ = LoadAsync();
	});

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ReanalyzeMultiSelectedCommand
	{
		get
		{
			//IL_0023: Unknown result type (might be due to invalid IL or missing references)
			//IL_0028: Unknown result type (might be due to invalid IL or missing references)
			//IL_002a: Expected O, but got Unknown
			//IL_002f: Expected O, but got Unknown
			AsyncRelayCommand obj = reanalyzeMultiSelectedCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)ReanalyzeMultiSelectedAsync, (Func<bool>)(() => HasMultiSelection));
				AsyncRelayCommand val2 = val;
				reanalyzeMultiSelectedCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ReanalyzeOutdatedCommand
	{
		get
		{
			//IL_0023: Unknown result type (might be due to invalid IL or missing references)
			//IL_0028: Unknown result type (might be due to invalid IL or missing references)
			//IL_002a: Expected O, but got Unknown
			//IL_002f: Expected O, but got Unknown
			AsyncRelayCommand obj = reanalyzeOutdatedCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)ReanalyzeOutdatedAsync, (Func<bool>)CanRunBulkReanalyze);
				AsyncRelayCommand val2 = val;
				reanalyzeOutdatedCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand NavigatePreviousCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = navigatePreviousCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)NavigatePrevious);
				RelayCommand val2 = val;
				navigatePreviousCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand NavigateNextCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = navigateNextCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)NavigateNext);
				RelayCommand val2 = val;
				navigateNextCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand<int> NavigateByOffsetCommand => (IRelayCommand<int>)(object)(navigateByOffsetCommand ?? (navigateByOffsetCommand = new RelayCommand<int>((Action<int>)NavigateByOffset)));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand OpenInExplorerCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = openInExplorerCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)OpenInExplorer);
				RelayCommand val2 = val;
				openInExplorerCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand CopyFullPathCommand
	{
		get
		{
			//IL_0023: Unknown result type (might be due to invalid IL or missing references)
			//IL_0028: Unknown result type (might be due to invalid IL or missing references)
			//IL_002a: Expected O, but got Unknown
			//IL_002f: Expected O, but got Unknown
			RelayCommand obj = copyFullPathCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)CopyFullPath, (Func<bool>)CanFileOp);
				RelayCommand val2 = val;
				copyFullPathCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand CopyToFolderCommand
	{
		get
		{
			//IL_0023: Unknown result type (might be due to invalid IL or missing references)
			//IL_0028: Unknown result type (might be due to invalid IL or missing references)
			//IL_002a: Expected O, but got Unknown
			//IL_002f: Expected O, but got Unknown
			RelayCommand obj = copyToFolderCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)CopyToFolder, (Func<bool>)CanFileOp);
				RelayCommand val2 = val;
				copyToFolderCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand SendToEmailCommand
	{
		get
		{
			//IL_0023: Unknown result type (might be due to invalid IL or missing references)
			//IL_0028: Unknown result type (might be due to invalid IL or missing references)
			//IL_002a: Expected O, but got Unknown
			//IL_002f: Expected O, but got Unknown
			RelayCommand obj = sendToEmailCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)SendToEmail, (Func<bool>)CanFileOp);
				RelayCommand val2 = val;
				sendToEmailCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand SendToPrintCommand
	{
		get
		{
			//IL_0023: Unknown result type (might be due to invalid IL or missing references)
			//IL_0028: Unknown result type (might be due to invalid IL or missing references)
			//IL_002a: Expected O, but got Unknown
			//IL_002f: Expected O, but got Unknown
			RelayCommand obj = sendToPrintCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)SendToPrint, (Func<bool>)CanFileOp);
				RelayCommand val2 = val;
				sendToPrintCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand SetAsWallpaperCommand
	{
		get
		{
			//IL_0023: Unknown result type (might be due to invalid IL or missing references)
			//IL_0028: Unknown result type (might be due to invalid IL or missing references)
			//IL_002a: Expected O, but got Unknown
			//IL_002f: Expected O, but got Unknown
			RelayCommand obj = setAsWallpaperCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)SetAsWallpaper, (Func<bool>)CanFileOp);
				RelayCommand val2 = val;
				setAsWallpaperCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand SendToOpenWithCommand
	{
		get
		{
			//IL_0023: Unknown result type (might be due to invalid IL or missing references)
			//IL_0028: Unknown result type (might be due to invalid IL or missing references)
			//IL_002a: Expected O, but got Unknown
			//IL_002f: Expected O, but got Unknown
			RelayCommand obj = sendToOpenWithCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)SendToOpenWith, (Func<bool>)CanFileOp);
				RelayCommand val2 = val;
				sendToOpenWithCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand<string> OpenInExternalEditorCommand => (IRelayCommand<string>)(object)(openInExternalEditorCommand ?? (openInExternalEditorCommand = new RelayCommand<string>((Action<string>)OpenInExternalEditor, (Predicate<string>)((string? _) => CanFileOp()))));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ExcludeFolderCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = excludeFolderCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)ExcludeFolderAsync);
				AsyncRelayCommand val2 = val;
				excludeFolderCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand IncludeFolderCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = includeFolderCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)IncludeFolderAsync);
				AsyncRelayCommand val2 = val;
				includeFolderCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ExcludeImageCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = excludeImageCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)ExcludeImageAsync);
				AsyncRelayCommand val2 = val;
				excludeImageCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand RemoveFromLibraryCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = removeFromLibraryCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)RemoveFromLibraryAsync);
				AsyncRelayCommand val2 = val;
				removeFromLibraryCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand DeletePermanentlyCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = deletePermanentlyCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)DeletePermanentlyAsync);
				AsyncRelayCommand val2 = val;
				deletePermanentlyCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand RegenerateThumbnailsCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = regenerateThumbnailsCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)RegenerateThumbnailsAsync);
				AsyncRelayCommand val2 = val;
				regenerateThumbnailsCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand OpenPhotoViewerCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = openPhotoViewerCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)OpenPhotoViewer);
				RelayCommand val2 = val;
				openPhotoViewerCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand OpenSettingsCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = openSettingsCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)OpenSettingsAsync);
				AsyncRelayCommand val2 = val;
				openSettingsCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand<IReadOnlyList<Guid>?> OpenBackupWindowCommand => (IAsyncRelayCommand<IReadOnlyList<Guid>?>)(object)(openBackupWindowCommand ?? (openBackupWindowCommand = new AsyncRelayCommand<IReadOnlyList<Guid>>((Func<IReadOnlyList<Guid>, Task>)OpenBackupWindowAsync)));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand<LibraryNodeViewModel?> OpenBackupWindowForAlbumCommand => (IAsyncRelayCommand<LibraryNodeViewModel?>)(object)(openBackupWindowForAlbumCommand ?? (openBackupWindowForAlbumCommand = new AsyncRelayCommand<LibraryNodeViewModel>((Func<LibraryNodeViewModel, Task>)OpenBackupWindowForAlbumAsync)));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ScanDrivesCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = scanDrivesCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)ScanDrivesAsync);
				AsyncRelayCommand val2 = val;
				scanDrivesCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand FindDuplicatesCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = findDuplicatesCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)FindDuplicatesAsync);
				AsyncRelayCommand val2 = val;
				findDuplicatesCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand RotateClockwiseCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = rotateClockwiseCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)RotateClockwiseAsync);
				AsyncRelayCommand val2 = val;
				rotateClockwiseCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand RotateCounterClockwiseCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = rotateCounterClockwiseCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)RotateCounterClockwiseAsync);
				AsyncRelayCommand val2 = val;
				rotateCounterClockwiseCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ToggleFavoriteCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = toggleFavoriteCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)ToggleFavoriteAsync);
				AsyncRelayCommand val2 = val;
				toggleFavoriteCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand<TagViewModel> RemoveTagCommand => (IAsyncRelayCommand<TagViewModel>)(object)(removeTagCommand ?? (removeTagCommand = new AsyncRelayCommand<TagViewModel>((Func<TagViewModel, Task>)RemoveTagAsync)));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand LoadAllMetadataCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = loadAllMetadataCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)LoadAllMetadataAsync);
				AsyncRelayCommand val2 = val;
				loadAllMetadataCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ShowAddTagPopupCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = showAddTagPopupCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)ShowAddTagPopup);
				AsyncRelayCommand val2 = val;
				showAddTagPopupCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand ConfirmBatchTagCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = confirmBatchTagCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)ConfirmBatchTag);
				AsyncRelayCommand val2 = val;
				confirmBatchTagCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand CloseAddTagPopupCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			RelayCommand obj = closeAddTagPopupCommand;
			if (obj == null)
			{
				RelayCommand val = new RelayCommand((Action)CloseAddTagPopup);
				RelayCommand val2 = val;
				closeAddTagPopupCommand = val;
				obj = val2;
			}
			return (IRelayCommand)(object)obj;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IRelayCommand<string> SelectTagSuggestionCommand => (IRelayCommand<string>)(object)(selectTagSuggestionCommand ?? (selectTagSuggestionCommand = new RelayCommand<string>((Action<string>)SelectTagSuggestion)));

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
	[ExcludeFromCodeCoverage]
	public IAsyncRelayCommand AddTagCommand
	{
		get
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Expected O, but got Unknown
			//IL_0023: Expected O, but got Unknown
			AsyncRelayCommand obj = addTagCommand;
			if (obj == null)
			{
				AsyncRelayCommand val = new AsyncRelayCommand((Func<Task>)AddTagAsync);
				AsyncRelayCommand val2 = val;
				addTagCommand = val;
				obj = val2;
			}
			return (IAsyncRelayCommand)(object)obj;
		}
	}

	public event Action<MediaFile>? ScrollIntoViewRequested;

	public event Action? ScrollToTopRequested;

	/// <summary>
	/// Fired after MediaFiles is replaced with a new ObservableCollection so the gallery
	/// code-behind can force the VirtualizingWrapPanel to remeasure all item containers.
	/// Without this, recycled containers retain their old measured sizes (e.g. search result
	/// thumbnails stay tiny after returning to the full library).
	/// </summary>
	public event Action? GalleryCollectionReplaced;

	private void ScheduleSimilarityRefresh()
	{
		try
		{
			_similarityRefreshCts.Cancel();
		}
		catch
		{
		}
		try
		{
			_similarityRefreshCts.Dispose();
		}
		catch
		{
		}
		_similarityRefreshCts = new CancellationTokenSource();
		CancellationToken ct = _similarityRefreshCts.Token;
		Task.Run(async delegate
		{
			_ = 1;
			try
			{
				await Task.Delay(400, ct);
				await ((DispatcherObject)Application.Current).Dispatcher.InvokeAsync((Action)delegate
				{
					((ICommand)FindSimilarCommand).Execute((object?)null);
				});
			}
			catch (OperationCanceledException)
			{
			}
		});
	}

	private void ScheduleRelatedRefresh()
	{
		if (!IsRelatedImagesMode)
		{
			return;
		}
		try
		{
			_relatedRefreshCts.Cancel();
		}
		catch
		{
		}
		try
		{
			_relatedRefreshCts.Dispose();
		}
		catch
		{
		}
		_relatedRefreshCts = new CancellationTokenSource();
		CancellationToken ct = _relatedRefreshCts.Token;
		Task.Run(async delegate
		{
			_ = 1;
			try
			{
				await Task.Delay(300, ct);
				await ((DispatcherObject)Application.Current).Dispatcher.InvokeAsync((Action)delegate
				{
					((ICommand)RefreshRelatedImagesCommand).Execute((object?)null);
				});
			}
			catch (OperationCanceledException)
			{
			}
		});
	}

	private static int TimeValueToMinutes(int value, RelatedTimeUnit unit)
	{
		return unit switch
		{
			RelatedTimeUnit.Minutes => value, 
			RelatedTimeUnit.Hours => value * 60, 
			RelatedTimeUnit.Days => value * 1440, 
			RelatedTimeUnit.Weeks => value * 10080, 
			RelatedTimeUnit.Months => value * 43200, 
			_ => value * 60, 
		};
	}

	public void StartAiSetup(IOllamaSetupService setupService, string modelName)
	{
		_aiSetupCts?.Cancel();
		_aiSetupCts = new CancellationTokenSource();
		RunAiSetupAsync(setupService, modelName, _aiSetupCts.Token);
	}

	private void RetryAiSetup()
	{
		_retryAiSetup?.Invoke();
	}

	public void SetAiSetupRetryAction(Action retry)
	{
		_retryAiSetup = retry;
	}

	private async Task RunAiSetupAsync(IOllamaSetupService setupService, string modelName, CancellationToken ct)
	{
		IsAiSetupRunning = true;
		IsAiSetupError = false;
		AiSetupProgress = -1.0;
		try
		{
			await foreach (OllamaSetupProgress item in setupService.EnsureReadyAsync(modelName, ct))
			{
				AiSetupMessage = item.Message;
				if (item.BytesTotal > 0)
				{
					AiSetupProgress = (double)item.BytesDownloaded / (double)item.BytesTotal * 100.0;
				}
				else
				{
					AiSetupProgress = -1.0;
				}
				if ((int)item.Stage == 5)
				{
					IsAiSetupRunning = false;
					IsAiSetupError = false;
					AiSetupMessage = "";
					// Silently pull the chat model now that Ollama is confirmed running.
					_ = setupService.EnsureChatModelAsync(UserPreferences.Current.ChatModelName, ct);
					return;
				}
				if ((int)item.Stage == 6)
				{
					IsAiSetupError = true;
					IsAiSetupRunning = false;
					return;
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex2)
		{
			AiSetupMessage = "Unexpected error: " + ex2.Message;
			IsAiSetupError = true;
			IsAiSetupRunning = false;
		}
	}

	private void CancelRelatedSearch()
	{
		_relatedCts?.Cancel();
	}

	private async Task FindRelatedImagesAsync()
	{
		if (SelectedMediaFile == null)
		{
			return;
		}
		IsSimilaritySearch = false;
		_similaritySourceId = null;
		SimilaritySourceName = "";
		PendingSearchQuery = "";
		SearchQuery = "";
		_relatedCts?.Cancel();
		_relatedCts = new CancellationTokenSource();
		CancellationToken ct = _relatedCts.Token;
		RelatedAnchorPhoto = SelectedMediaFile;
		_relatedPhotoIds.Clear();
		IsFindingRelated = true;
		try
		{
			await RunRelatedQueryAsync(ct);
			ct.ThrowIfCancellationRequested();
			IsRelatedImagesMode = true;
			await LoadAsync();
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			IsFindingRelated = false;
		}
	}

	private bool CanFindRelatedImages()
	{
		return SelectedMediaFile != null;
	}

	private async Task FindSimilarAsync()
	{
		if (SelectedMediaFile == null)
		{
			return;
		}
		Guid sourceId = SelectedMediaFile.Id;
		string sourceName = SelectedMediaFile.FileName;
		MediaFile sourceFile = SelectedMediaFile;
		IsRelatedImagesMode = false;
		RelatedAnchorPhoto = null;
		_relatedPhotoIds.Clear();
		_similaritySourceId = sourceId;
		SimilaritySourceName = sourceName;
		PendingSearchQuery = "Similar: " + sourceName;
		SearchQuery = PendingSearchQuery;
		IsSimilaritySearch = true;
		IsSemanticSearch = false;
		StatusText = "Finding photos similar to \"" + sourceName + "\"…";
		IsLoading = true;
		try
		{
			List<MediaFile> sorted = ApplySortOrder(await _semanticSearch.FindSimilarAsync(sourceId, SimilarityCount), _sortHistory).ToList();
			MediaFiles = new ObservableCollection<MediaFile>(sorted);
			GalleryCollectionReplaced?.Invoke();
			PhotoCount = sorted.Count;
			TotalLibraryCount = await WithRepo((IMediaFileRepository r) => ((IRepository<MediaFile>)(object)r).CountAsync());
			OnPropertyChanged("PhotoCountText");
			OnPropertyChanged("PhotoCountTooltip");
			OnPropertyChanged("HasNoResults");
			SelectedMediaFile = MediaFiles.FirstOrDefault((MediaFile m) => m.Id == sourceId) ?? sourceFile;
			this.ScrollToTopRequested?.Invoke();
			if (PhotoCount == 0)
			{
				StatusText = "No photos similar to \"" + sourceName + "\" found";
				return;
			}
			StatusText = $"Loading {PhotoCount} similar photo{((PhotoCount == 1) ? "" : "s")}…";
			using CancellationTokenSource thumbCts = new CancellationTokenSource();
			await LoadThumbnailsAsync(sorted.ToArray(), thumbCts.Token);
			StatusText = $"Found {PhotoCount} photo{((PhotoCount == 1) ? "" : "s")} similar to \"{sourceName}\"";
		}
		finally
		{
			IsLoading = false;
		}
	}

	private bool CanFindSimilar()
	{
		if (SelectedHasEmbedding)
		{
			if (IsSimilaritySearch)
			{
				return IsRelatedImagesMode;
			}
			return true;
		}
		return false;
	}

	private async Task ExitRelatedImagesModeAsync()
	{
		MediaFile scrollTarget = SelectedMediaFile;
		IsRelatedImagesMode = false;
		RelatedAnchorPhoto = null;
		_relatedPhotoIds.Clear();
		await LoadAsync();
		MediaFile val = ((scrollTarget != null) ? MediaFiles.FirstOrDefault((MediaFile m) => m.Id == scrollTarget.Id) : null) ?? SelectedMediaFile;
		if (val != null)
		{
			this.ScrollIntoViewRequested?.Invoke(val);
		}
	}

	private async Task SaveRelatedAsAlbumAsync()
	{
		if (!IsRelatedImagesMode || _relatedPhotoIds.Count == 0)
		{
			return;
		}
		string suggestedName = ((RelatedAnchorPhoto == null) ? "Related Photos" : (RelatedAnchorPhoto.DateTaken.HasValue ? $"Photos from {RelatedAnchorPhoto.DateTaken.Value:MMM d, yyyy}" : ("Related to " + RelatedAnchorPhoto.FileName)));
		IReadOnlyList<Library> readOnlyList = await WithLibRepo((ILibraryRepository r) => r.GetAllAsync());
		if (readOnlyList.Count != 0)
		{
			Guid libraryId = readOnlyList[0].Id;
			Album album = await WithLibRepo((ILibraryRepository r) => r.CreateAlbumAsync(libraryId, suggestedName, (string)null));
			List<Guid> photoIds = _relatedPhotoIds.ToList();
			await WithLibRepo((ILibraryRepository r) => r.AddPhotosToAlbumAsync(album.Id, (IReadOnlyList<Guid>)photoIds));
			IsRelatedImagesMode = false;
			RelatedAnchorPhoto = null;
			_relatedPhotoIds.Clear();
			await LoadSidebarLibrariesAsync();
			NavigateToAlbum(album.Id, suggestedName);
			StatusText = $"Created album \"{suggestedName}\" with {photoIds.Count} photos";
		}
	}

	private async Task RefreshRelatedImagesAsync()
	{
		if (IsRelatedImagesMode)
		{
			_relatedPhotoIds.Clear();
			await RunRelatedQueryAsync();
			await LoadAsync();
		}
	}

	private async Task RunRelatedQueryAsync(CancellationToken ct = default(CancellationToken))
	{
		if (RelatedAnchorPhoto == null)
		{
			return;
		}
		_relatedPhotoIds.Add(RelatedAnchorPhoto.Id);
		int timeMinutes = TimeValueToMinutes(Math.Max(1, RelatedTimeValue), (RelatedTimeUnit)RelatedTimeUnitIndex);
		double radiusKm = (double)_distStepsMeters[Math.Clamp(RelatedDistanceStepIndex, 0, _distStepsMeters.Length - 1)] / 1000.0;
		DateTime anchorDate = RelatedAnchorPhoto.DateTaken ?? RelatedAnchorPhoto.DateImported;
		if (RelatedTimeEnabled)
		{
			ct.ThrowIfCancellationRequested();
			foreach (MediaFile item in await WithRepo((IMediaFileRepository r) => r.GetNearbyDateAsync(anchorDate, timeMinutes, RelatedAnchorPhoto.Id, 5000)))
			{
				_relatedPhotoIds.Add(item.Id);
			}
		}
		if (!RelatedLocationEnabled)
		{
			return;
		}
		double? latitude = RelatedAnchorPhoto.Latitude;
		if (!latitude.HasValue)
		{
			return;
		}
		double lat = latitude.GetValueOrDefault();
		latitude = RelatedAnchorPhoto.Longitude;
		if (!latitude.HasValue)
		{
			return;
		}
		double lon = latitude.GetValueOrDefault();
		ct.ThrowIfCancellationRequested();
		foreach (MediaFile item2 in await WithRepo((IMediaFileRepository r) => r.GetNearbyLocationAsync(lat, lon, radiusKm, RelatedAnchorPhoto.Id, 5000)))
		{
			_relatedPhotoIds.Add(item2.Id);
		}
	}

	private async Task ClearPersonFilterAsync()
	{
		IsPersonFilterActive = false;
		ActiveFilterLabel = "";
		_personFilterPhotoIds = Array.Empty<Guid>();
		await LoadAsync();
	}

	private void RefreshDescriptionProperties()
	{
		MediaFile? selectedMediaFile = SelectedMediaFile;
		object obj = ((selectedMediaFile != null) ? selectedMediaFile.UserDescription : null);
		if (obj == null)
		{
			MediaFile? selectedMediaFile2 = SelectedMediaFile;
			obj = ((selectedMediaFile2 != null) ? selectedMediaFile2.AiDescription : null) ?? "";
		}
		string text = (string)obj;
		MediaFile? selectedMediaFile3 = SelectedMediaFile;
		bool flag = ((selectedMediaFile3 != null) ? selectedMediaFile3.UserDescription : null) != null;
		object obj2;
		if (!flag)
		{
			MediaFile? selectedMediaFile4 = SelectedMediaFile;
			if (!string.IsNullOrEmpty((selectedMediaFile4 != null) ? selectedMediaFile4.AiModelUsed : null))
			{
				obj2 = "via " + SelectedMediaFile.AiModelUsed;
				goto IL_0080;
			}
		}
		obj2 = "";
		goto IL_0080;
		IL_0080:
		string text2 = (string)obj2;
		DisplayDescription = text;
		HasDisplayDescription = !string.IsNullOrEmpty(text);
		HasUserDescription = flag;
		DescriptionLabel = (flag ? "YOUR DESCRIPTION" : "AI DESCRIPTION");
		DescriptionModelLine = text2;
		HasDescriptionModelLine = !string.IsNullOrEmpty(text2);
		MediaFile selectedMediaFile5 = SelectedMediaFile;
		if (!flag && selectedMediaFile5 != null && selectedMediaFile5.AiDescription != null && selectedMediaFile5.AiDescription != string.Empty)
		{
			string text3 = AppSettings.VisionModelName;
			string text4 = AppSettings.CurrentPromptVersion;
			bool num = selectedMediaFile5.AiModelUsed == text3;
			bool flag2 = selectedMediaFile5.PromptVersion == text4;
			bool flag3 = num && flag2;
			IsDescriptionCurrent = flag3;
			IsDescriptionStale = !flag3;
			DescriptionFreshnessTooltip = (flag3 ? ("Current — " + text3 + " / " + text4) : $"Outdated — generated with {selectedMediaFile5.AiModelUsed ?? "unknown model"} / {selectedMediaFile5.PromptVersion ?? "unknown prompt"}\nCurrent: {text3} / {text4}\nRe-analyze to update.");
		}
		else
		{
			IsDescriptionCurrent = false;
			IsDescriptionStale = false;
			DescriptionFreshnessTooltip = "";
		}
	}

	private void RefreshCaptionProperties()
	{
		MediaFile? selectedMediaFile = SelectedMediaFile;
		string value = (DisplayCaption = ((selectedMediaFile != null) ? selectedMediaFile.UserCaption : null) ?? "");
		HasDisplayCaption = !string.IsNullOrEmpty(value);
	}

	private void OpenInMaps()
	{
		MediaFile selectedMediaFile = SelectedMediaFile;
		if (selectedMediaFile == null)
		{
			return;
		}
		double? latitude = selectedMediaFile.Latitude;
		if (!latitude.HasValue)
		{
			return;
		}
		double valueOrDefault = latitude.GetValueOrDefault();
		double? longitude = selectedMediaFile.Longitude;
		if (longitude.HasValue)
		{
			double valueOrDefault2 = longitude.GetValueOrDefault();
			string fileName = "https://maps.google.com/maps?q=" + valueOrDefault.ToString(CultureInfo.InvariantCulture) + "," + valueOrDefault2.ToString(CultureInfo.InvariantCulture);
			try
			{
				Process.Start(new ProcessStartInfo(fileName)
				{
					UseShellExecute = true
				});
			}
			catch
			{
			}
		}
	}

	private string GetSortLabel(GallerySortField field)
	{
		int num = _sortHistory.FindIndex(((GallerySortField Field, bool Descending) e) => e.Field == field);
		if (num < 0)
		{
			return "";
		}
		string text = (_sortHistory[num].Descending ? "↓" : "↑");
		if (_sortHistory.Count <= 1)
		{
			return text;
		}
		return $"{num + 1}{text}";
	}

	private bool IsSortActive(GallerySortField field)
	{
		return _sortHistory.Any(((GallerySortField Field, bool Descending) e) => e.Field == field);
	}

	private void SortByDateTaken()
	{
		ApplySortFieldAsync(GallerySortField.DateTaken);
	}

	private void SortByFileName()
	{
		ApplySortFieldAsync(GallerySortField.FileName);
	}

	private void SortByFileSize()
	{
		ApplySortFieldAsync(GallerySortField.FileSize);
	}

	private void SortByDateImported()
	{
		ApplySortFieldAsync(GallerySortField.DateImported);
	}

	private void SortByCamera()
	{
		ApplySortFieldAsync(GallerySortField.Camera);
	}

	private void ApplySortFieldAsync(GallerySortField field)
	{
		GallerySortField gallerySortField = field;
		bool flag = ((gallerySortField == GallerySortField.DateTaken || (uint)(gallerySortField - 2) <= 1u) ? true : false);
		bool flag2 = flag;
		int num = _sortHistory.FindIndex(((GallerySortField Field, bool Descending) e) => e.Field == field);
		if (num < 0)
		{
			_sortHistory.Insert(0, (field, flag2));
		}
		else if (_sortHistory[num].Descending == flag2)
		{
			_sortHistory[num] = (field, !flag2);
		}
		else
		{
			_sortHistory.RemoveAt(num);
		}
		PersistSortHistory();
		NotifySortPropertiesChanged();
		ReSortGallery();
	}

	private void ReSortGallery()
	{
		MediaFile? selectedMediaFile = SelectedMediaFile;
		Guid? prevSelectedId = ((selectedMediaFile != null) ? new Guid?(selectedMediaFile.Id) : ((Guid?)null));
		List<MediaFile> list = ApplySortOrder(MediaFiles.ToList(), _sortHistory).ToList();
		MediaFiles = new ObservableCollection<MediaFile>(list);
		GalleryCollectionReplaced?.Invoke();
		if (prevSelectedId.HasValue)
		{
			SelectedMediaFile = MediaFiles.FirstOrDefault((MediaFile m) => m.Id == prevSelectedId.Value);
		}
	}

	private void PersistSortHistory()
	{
		UserPreferences.Current.GallerySortHistory = _sortHistory.Select(((GallerySortField Field, bool Descending) e) => $"{e.Field}:{(e.Descending ? "desc" : "asc")}").ToList();
		UserPreferences.Current.Save();
	}

	private void NotifySortPropertiesChanged()
	{
		OnPropertyChanged("DateTakenSortLabel");
		OnPropertyChanged("FileNameSortLabel");
		OnPropertyChanged("FileSizeSortLabel");
		OnPropertyChanged("DateImportedSortLabel");
		OnPropertyChanged("CameraSortLabel");
		OnPropertyChanged("DateTakenSortActive");
		OnPropertyChanged("FileNameSortActive");
		OnPropertyChanged("FileSizeSortActive");
		OnPropertyChanged("DateImportedSortActive");
		OnPropertyChanged("CameraSortActive");
	}

	private static IEnumerable<MediaFile> ApplySortOrder(IEnumerable<MediaFile> source, List<(GallerySortField Field, bool Descending)> history)
	{
		if (history.Count == 0)
		{
			return source;
		}
		(GallerySortField Field, bool Descending) first = history[0];
		IOrderedEnumerable<MediaFile> orderedEnumerable = (first.Descending ? source.OrderByDescending((MediaFile m) => SortKey(m, first.Field)) : source.OrderBy((MediaFile m) => SortKey(m, first.Field)));
		for (int num = 1; num < history.Count; num++)
		{
			(GallerySortField Field, bool Descending) e = history[num];
			orderedEnumerable = (e.Descending ? orderedEnumerable.ThenByDescending((MediaFile m) => SortKey(m, e.Field)) : orderedEnumerable.ThenBy((MediaFile m) => SortKey(m, e.Field)));
		}
		return orderedEnumerable;
	}

	private static IComparable SortKey(MediaFile m, GallerySortField field)
	{
		return field switch
		{
			GallerySortField.FileName => m.FileName, 
			GallerySortField.FileSize => m.FileSize, 
			GallerySortField.DateImported => m.DateImported, 
			GallerySortField.Camera => m.CameraModel ?? m.CameraMake ?? "\uffff", 
			_ => m.DateTaken ?? m.DateImported, 
		};
	}

	private static List<(GallerySortField Field, bool Descending)> LoadSortHistory()
	{
		List<(GallerySortField, bool)> list = new List<(GallerySortField, bool)>();
		foreach (string item in UserPreferences.Current.GallerySortHistory)
		{
			string[] array = item.Split(':');
			if (array.Length == 2 && Enum.TryParse<GallerySortField>(array[0], out var result))
			{
				list.Add((result, array[1] == "desc"));
			}
		}
		if (list.Count == 0)
		{
			list.Add((GallerySortField.DateTaken, true));
		}
		return list;
	}

	public MainViewModel(IServiceScopeFactory scopeFactory, MetricsViewModel metrics, ISemanticSearchService semanticSearch, ImportProgressViewModel progress, IFolderWatcherService folderWatcher, IExifEditService exifEditService)
	{
		_scopeFactory = scopeFactory;
		Metrics = metrics;
		_semanticSearch = semanticSearch;
		Progress = progress;
		_folderWatcher = folderWatcher;
		_exifEditService = exifEditService;
		_sortHistory = LoadSortHistory();
		((ObservableObject)Progress).PropertyChanged += delegate(object? _, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == "IsRunning")
			{
				Application.Current.Dispatcher.InvokeAsync(() =>
				{
					((IRelayCommand)ReanalyzeAllCommand).NotifyCanExecuteChanged();
					((IRelayCommand)ReanalyzeOutdatedCommand).NotifyCanExecuteChanged();
				});
			}
		};
		LoadAsync();
		LoadSidebarLibrariesAsync();
		LoadExclusionCacheAsync();
		// ResumeImportQueueAsync is triggered from App.xaml.cs after onboarding completes,
		// so the dialog never races with FirstRunWindow.ShowDialog().
		VisionBackgroundWorkerAsync(_visionWorkerCts.Token);
		Task.Delay(TimeSpan.FromSeconds(5.0)).ContinueWith((Task _) => FaceDetectionWorkerAsync(_faceWorkerCts.Token), TaskScheduler.Default).Unwrap();
		StartupHealAsync();
		ResumeUpdateOutdatedAsync();
		StartPeriodicHealTimer();
		// Refresh the FTS index at startup (non-destructive — re-upserts row-by-row so searches
		// remain fully functional throughout). Clears stale folder data from existing entries
		// and picks up description text fixed by the startup SQL double-comma migration.
		RefreshFtsIndexBackgroundAsync();
		StartFolderWatcherAsync();
		RebuildSendToMenuItems();
	}

	private async Task<T> WithRepo<T>(Func<IMediaFileRepository, Task<T>> op)
	{
		using IServiceScope scope = _scopeFactory.CreateScope();
		return await op(scope.ServiceProvider.GetRequiredService<IMediaFileRepository>());
	}

	private async Task WithRepo(Func<IMediaFileRepository, Task> op)
	{
		using IServiceScope scope = _scopeFactory.CreateScope();
		await op(scope.ServiceProvider.GetRequiredService<IMediaFileRepository>());
	}

	private async Task<T> WithLibRepo<T>(Func<ILibraryRepository, Task<T>> op)
	{
		using IServiceScope scope = _scopeFactory.CreateScope();
		return await op(scope.ServiceProvider.GetRequiredService<ILibraryRepository>());
	}

	private async Task WithLibRepo(Func<ILibraryRepository, Task> op)
	{
		using IServiceScope scope = _scopeFactory.CreateScope();
		await op(scope.ServiceProvider.GetRequiredService<ILibraryRepository>());
	}

	private async Task StartupHealAsync()
	{
		await Task.Delay(TimeSpan.FromSeconds(10.0));
		if (!File.Exists(AppSettings.ImportQueuePath))
			await HealUnanalyzedAsync();
	}

	private async Task ResumeUpdateOutdatedAsync()
	{
		if (File.Exists(AppSettings.UpdateOutdatedFlagPath))
		{
			bool skipUserModified = false;
			try
			{
				skipUserModified = (await File.ReadAllTextAsync(AppSettings.UpdateOutdatedFlagPath)).Trim() == "1";
			}
			catch
			{
			}
			await Task.Delay(TimeSpan.FromSeconds(15.0));
			// Skip if an import queue is pending — the import will be running or about to run,
			// and starting a re-analysis now would block it via the IsRunning guard.
			if (!Progress.IsRunning && !File.Exists(AppSettings.ImportQueuePath))
			{
				AttachTaskbar(Progress);
				AttachMetrics(Progress);
				AttachPhotoCallback(Progress);
				await Progress.RunReanalyzeOutdatedAsync(skipUserModified);
				ClearTaskbar();
				Metrics.ClearBatchStatus();
				StatusText = Progress.ResultMessage;
				await RefreshAfterBulkReanalysisAsync();
			}
		}
	}

	private async Task RebuildSearchIndexBackgroundAsync()
	{
		StatusText = "Rebuilding search index…";
		try
		{
			await Task.Run(async delegate
			{
				using IServiceScope scope = _scopeFactory.CreateScope();
				await scope.ServiceProvider.GetRequiredService<IMediaFileRepository>().RebuildFtsIndexAsync();
			});
			StatusText = "Search index rebuilt — all photos are now searchable.";
		}
		catch (Exception ex)
		{
			StatusText = "Search index rebuild failed: " + ex.Message;
			AppLog.Error("RebuildSearchIndex: " + ex.Message);
		}
	}

	private async Task RefreshFtsIndexBackgroundAsync()
	{
		IsFtsRefreshing = true;
		try
		{
			await Task.Run(async delegate
			{
				using IServiceScope scope = _scopeFactory.CreateScope();
				await scope.ServiceProvider.GetRequiredService<IMediaFileRepository>().RefreshFtsIndexAsync();
			});
		}
		catch (Exception ex)
		{
			AppLog.Error("RefreshFtsIndex: " + ex.Message);
		}
		finally
		{
			IsFtsRefreshing = false;
		}
	}

	private void StartPeriodicHealTimer()
	{
		//IL_0013: Unknown result type (might be due to invalid IL or missing references)
		//IL_0018: Unknown result type (might be due to invalid IL or missing references)
		//IL_0031: Expected O, but got Unknown
		_healTimerTick = async delegate
		{
			await HealUnanalyzedAsync();
		};
		_healTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMinutes(30.0)
		};
		_healTimer.Tick += _healTimerTick;
		_healTimer.Start();
	}

	private async Task StartFolderWatcherAsync()
	{
		await Task.Delay(3000);
		_folderWatcher.OnPhotoImported = delegate(MediaFile mf)
		{
			((DispatcherObject)Application.Current).Dispatcher.InvokeAsync((Action)delegate
			{
				if (MediaFiles.FirstOrDefault((MediaFile m) => m.Id == mf.Id) == null)
				{
					MediaFiles.Insert(0, mf);
				}
				PhotoCount = MediaFiles.Count;
				StatusText = "Auto-imported: " + mf.FileName;
			});
		};
		await _folderWatcher.StartAsync(default(CancellationToken));
	}

	public void Dispose()
	{
		try
		{
			if (_healTimer != null)
			{
				_healTimer.Stop();
				if (_healTimerTick != null)
				{
					_healTimer.Tick -= _healTimerTick;
				}
				_healTimer = null;
			}
			_visionWorkerCts.Cancel();
			_faceWorkerCts.Cancel();
			_relatedRefreshCts.Cancel();
			_loadCts.Cancel();
			Task.Delay(100).ContinueWith(delegate
			{
				try
				{
					_loadCts.Dispose();
				}
				catch
				{
				}
			});
			_folderWatcher.Stop();
		}
		catch (Exception value)
		{
			AppLog.Error($"Error during MainViewModel.Dispose: {value}");
		}
	}

	private async Task HealUnanalyzedAsync()
	{
		if (Progress.IsRunning)
			return;
		if (File.Exists(AppSettings.ImportQueuePath))
			return;

		List<MediaFile> list = (await WithRepo((IMediaFileRepository r) => r.GetUnanalyzedAsync(int.MaxValue))).Where((MediaFile m) => (int)m.AnalysisStatus == 0).ToList();
		if (list.Count != 0)
		{
			StatusText = $"Auto-healing {list.Count} unanalyzed photo(s)…";
			AttachTaskbar(Progress);
			AttachMetrics(Progress);
			AttachPhotoCallback(Progress);
			using (await AcquireAnalysisLockAsync("HealUnanalyzed"))
			{
				await Progress.RunReanalyzeAsync(unanalyzedOnly: true);
			}
			ClearTaskbar();
			Metrics.ClearBatchStatus();
			StatusText = Progress.ResultMessage;
			await LoadAsync();
		}
	}

	private async Task LoadAsync()
	{
		if (IsSimilaritySearch)
		{
			return;
		}
		try
		{
			_loadCts.Cancel();
		}
		catch
		{
		}
		try
		{
			_loadCts.Dispose();
		}
		catch
		{
		}
		_loadCts = new CancellationTokenSource();
		CancellationToken ct = _loadCts.Token;
		if (!IsRelatedImagesMode)
		{
			MultiSelectedItems.Clear();
			_selectionAnchor = null;
			NotifyMultiSelectChanged();
		}
		IsLoading = true;
		try
		{
			IEnumerable<MediaFile> source;
			if (!string.IsNullOrWhiteSpace(SearchQuery))
			{
				string capturedQuery = SearchQuery;

				// Strip drive: and imported: prefixes before the date/FTS pipeline sees them.
				var prefixTokens = SearchPrefixParser.Parse(capturedQuery);
				string innerQuery = prefixTokens.RemainingQuery;

				if (prefixTokens.ImportedRange != null)
				{
					// imported: prefix — filter explicitly by DateImported field.
					List<MediaFile> importedPhotos = (await WithRepo((IMediaFileRepository r) => r.GetByImportedDateRangeAsync(prefixTokens.ImportedRange.From, prefixTokens.ImportedRange.To))).ToList();
					ct.ThrowIfCancellationRequested();
					if (!string.IsNullOrWhiteSpace(innerQuery))
					{
						HashSet<Guid> ftsIds = (await WithRepo((IMediaFileRepository r) => r.SearchAsync(innerQuery))).Select((MediaFile m) => m.Id).ToHashSet();
						ct.ThrowIfCancellationRequested();
						source = importedPhotos.Where((MediaFile m) => ftsIds.Contains(m.Id));
					}
					else
					{
						source = importedPhotos;
					}
					IsSemanticSearch = false;
				}
				else if (!string.IsNullOrWhiteSpace(innerQuery))
				{
					var dateResult = DateSearchParser.TryParse(innerQuery);
					if (dateResult != null)
					{
						List<MediaFile> datePhotos = (await WithRepo((IMediaFileRepository r) => r.GetByDateRangeAsync(dateResult.Range.From, dateResult.Range.To))).ToList();
						ct.ThrowIfCancellationRequested();
						if (!string.IsNullOrWhiteSpace(dateResult.RemainingQuery))
						{
							HashSet<Guid> ftsIds = (await WithRepo((IMediaFileRepository r) => r.SearchAsync(dateResult.RemainingQuery))).Select((MediaFile m) => m.Id).ToHashSet();
							ct.ThrowIfCancellationRequested();
							source = datePhotos.Where((MediaFile m) => ftsIds.Contains(m.Id));
						}
						else
						{
							source = datePhotos;
						}
						IsSemanticSearch = false;
					}
					else
					{
						List<MediaFile> list = (await WithRepo((IMediaFileRepository r) => r.SearchAsync(innerQuery))).ToList();
						ct.ThrowIfCancellationRequested();
						if (list.Count > 0)
						{
							source = list;
							IsSemanticSearch = false;
						}
						else
						{
							Task<IReadOnlyList<MediaFile>> semanticTask = _semanticSearch.SearchAsync(innerQuery, 50);
							await Task.WhenAny(semanticTask, Task.Delay(TimeSpan.FromSeconds(5.0), ct));
							ct.ThrowIfCancellationRequested();
							IReadOnlyList<MediaFile> readOnlyList2;
							if (!semanticTask.IsCompletedSuccessfully)
							{
								IReadOnlyList<MediaFile> readOnlyList = Array.Empty<MediaFile>();
								readOnlyList2 = readOnlyList;
							}
							else
							{
								readOnlyList2 = semanticTask.Result;
							}
							IReadOnlyList<MediaFile> readOnlyList3 = readOnlyList2;
							IEnumerable<MediaFile> enumerable2;
							if (readOnlyList3.Count <= 0)
							{
								IEnumerable<MediaFile> enumerable = Array.Empty<MediaFile>();
								enumerable2 = enumerable;
							}
							else
							{
								IEnumerable<MediaFile> enumerable = readOnlyList3;
								enumerable2 = enumerable;
							}
							source = enumerable2;
							IsSemanticSearch = readOnlyList3.Count > 0;
						}
					}
				}
				else
				{
					// drive: only — no other query terms; show full gallery for drive filtering below.
					source = await GetGalleryViewPhotosAsync();
					IsSemanticSearch = false;
				}

				// Apply drive filter (works regardless of which path above set source).
				if (prefixTokens.DriveRoot != null)
					source = source.Where((MediaFile m) => m.FilePath.StartsWith(prefixTokens.DriveRoot, StringComparison.OrdinalIgnoreCase));
			}
			else if (IsRelatedImagesMode)
			{
				IsSemanticSearch = false;
				IReadOnlyList<MediaFile> readOnlyList = ((_relatedPhotoIds.Count <= 0) ? Array.Empty<MediaFile>() : (await WithRepo((IMediaFileRepository r) => r.GetByIdsAsync((IEnumerable<Guid>)_relatedPhotoIds))));
				source = readOnlyList;
			}
			else
			{
				IsSemanticSearch = false;
				source = await GetGalleryViewPhotosAsync();
			}
			if (IsRelatedImagesMode && _relatedPhotoIds.Count > 0 && !string.IsNullOrWhiteSpace(SearchQuery))
			{
				source = source.Where((MediaFile m) => _relatedPhotoIds.Contains(m.Id));
			}
			if (IsPersonFilterActive && _personFilterPhotoIds.Count > 0)
			{
				HashSet<Guid> personIdSet = _personFilterPhotoIds.ToHashSet();
				source = source.Where((MediaFile m) => personIdSet.Contains(m.Id));
			}
			ct.ThrowIfCancellationRequested();
			MediaFile? selectedMediaFile = SelectedMediaFile;
			Guid? prevSelectedId = ((selectedMediaFile != null) ? new Guid?(selectedMediaFile.Id) : ((Guid?)null));
			List<MediaFile> photoList = ApplySortOrder(source, _sortHistory).ToList();
			MediaFiles = new ObservableCollection<MediaFile>(photoList);
			GalleryCollectionReplaced?.Invoke();
			PhotoCount = photoList.Count;
			int totalLibraryCount = ((!IsSearchActive) ? PhotoCount : (await WithRepo((IMediaFileRepository r) => ((IRepository<MediaFile>)(object)r).CountAsync())));
			TotalLibraryCount = totalLibraryCount;
			OnPropertyChanged("PhotoCountText");
			OnPropertyChanged("PhotoCountTooltip");
			OnPropertyChanged("ExpressLimitText");
			PendingDescriptionCount = photoList.Count((MediaFile m) => (int)m.AnalysisStatus == 4);
			if (IsRelatedImagesMode)
			{
				this.ScrollToTopRequested?.Invoke();
			}
			if (prevSelectedId.HasValue)
			{
				MediaFile val = (SelectedMediaFile = MediaFiles.FirstOrDefault((MediaFile m) => m.Id == prevSelectedId.Value));
				if (val != null && !IsRelatedImagesMode)
				{
					this.ScrollIntoViewRequested?.Invoke(val);
				}
			}
			NotifyPropertiesChanged("IsEmpty", "IsLibraryEmpty", "HasNoResults", "IsRelatedNoResults", "IsSearchActive", "ShowSemanticUnavailableHint", "NoResultsMessage");
		}
		catch (OperationCanceledException)
		{
			IsLoading = false;
			return;
		}
		finally
		{
			IsLoading = false;
			if (!_initialLoadDone)
			{
				_initialLoadDone = true;
				if (StatusText == "Loading library…")
				{
					StatusText = $"{PhotoCount:N0} photos";
				}
			}
		}
		LoadDeferredAsync(MediaFiles.ToList()).ContinueWith(delegate(Task t)
		{
			AppLog.Error($"LoadDeferredAsync failed: {t.Exception.InnerException ?? t.Exception}");
		}, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
	}

	private async Task<IEnumerable<MediaFile>> GetGalleryViewPhotosAsync()
	{
		GalleryView activeView = ActiveView;
		return (activeView == GalleryView.Favorites) ? (await WithRepo((IMediaFileRepository r) => r.GetFavoritesAsync())) : ((activeView != GalleryView.Album || !ActiveAlbumId.HasValue) ? (await WithRepo((IMediaFileRepository r) => ((IRepository<MediaFile>)(object)r).GetAllAsync())) : (await WithLibRepo((ILibraryRepository r) => r.GetPhotosByAlbumAsync(ActiveAlbumId.Value))));
	}

	private async Task LoadDeferredAsync(List<MediaFile> snapshot)
	{
		OfflineCount = await Task.Run(() => MarkOfflineFiles(snapshot));
		NotifyPropertiesChanged("HasOfflineFiles", "OfflineStatusText", "SelectedIsOffline");
		OutdatedDescriptionCount = await WithRepo((IMediaFileRepository r) => r.CountOutdatedDescriptionsAsync(AppSettings.VisionModelName, AppSettings.CurrentPromptVersion, AppSettings.CurrentPostProcessVersion));
		foreach (Guid item in await WithRepo((IMediaFileRepository r) => r.GetVisionPendingIdsAsync()))
		{
			EnqueueForVision(item);
		}
	}

	private async Task<IDisposable> AcquireAnalysisLockAsync(string caller, CancellationToken ct = default(CancellationToken))
	{
		Stopwatch sw = Stopwatch.StartNew();
		AppLog.Info($"[LOCK] {caller}: waiting for _analysisLock (current count={_analysisLock.CurrentCount})");
		await _analysisLock.WaitAsync(ct);
		sw.Stop();
		AppLog.Info($"[LOCK] {caller}: acquired _analysisLock after {sw.ElapsedMilliseconds}ms");
		return new LockReleaser(_analysisLock, caller);
	}

	private async Task LoadThumbnailsAsync(MediaFile[] files, CancellationToken ct)
	{
		foreach (MediaFile mf in files)
		{
			if (ct.IsCancellationRequested)
			{
				break;
			}
			string path = mf.ThumbnailMedium ?? mf.ThumbnailSmall;
			if (path == null || !File.Exists(path))
			{
				continue;
			}
			object obj = await Task.Run(delegate
			{
				try
				{
					BitmapImage bitmapImage = new BitmapImage();
					bitmapImage.BeginInit();
					bitmapImage.UriSource = new Uri(path);
					bitmapImage.DecodePixelWidth = 512;
					bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
					bitmapImage.EndInit();
					((Freezable)bitmapImage).Freeze();
					return bitmapImage;
				}
				catch
				{
					return (object)null;
				}
			}, ct);
			if (obj != null && !ct.IsCancellationRequested)
			{
				mf.LoadedThumbnail = obj;
			}
		}
	}

	private async Task LoadSingleThumbnailAsync(MediaFile mf)
	{
		string path = mf.ThumbnailMedium ?? mf.ThumbnailSmall;
		if (path == null || !File.Exists(path))
		{
			return;
		}
		object obj = await Task.Run(delegate
		{
			try
			{
				BitmapImage bitmapImage = new BitmapImage();
				bitmapImage.BeginInit();
				bitmapImage.UriSource = new Uri(path);
				bitmapImage.DecodePixelWidth = 512;
				bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
				bitmapImage.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
				bitmapImage.EndInit();
				((Freezable)bitmapImage).Freeze();
				return bitmapImage;
			}
			catch
			{
				return (object)null;
			}
		});
		if (obj != null)
		{
			mf.LoadedThumbnail = obj;
		}
	}

	private void EnqueueForVision(Guid photoId)
	{
		if (!UserPreferences.Current.IsExpressMode)
		{
			_visionQueueNormal.Enqueue(photoId);
			_visionSignal.Release();
		}
	}

	private void PrioritizeVision(Guid photoId)
	{
		if (!UserPreferences.Current.IsExpressMode)
		{
			_visionQueuePriority.Enqueue(photoId);
			_visionSignal.Release();
		}
	}

	private async Task VisionBackgroundWorkerAsync(CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			try
			{
				await _visionSignal.WaitAsync(ct);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			if ((!_visionQueuePriority.TryDequeue(out var photoId) && !_visionQueueNormal.TryDequeue(out photoId)) || _visionSkipIds.ContainsKey(photoId))
			{
				continue;
			}
			bool lockAcquired = false;
			try
			{
				await Progress.WaitIfPausedAsync(ct);
				Stopwatch swLock = Stopwatch.StartNew();
				AppLog.Info($"[LOCK] VisionWorker: waiting for _analysisLock (photo {photoId})");
				await _analysisLock.WaitAsync(ct);
				lockAcquired = true;
				AppLog.Info($"[LOCK] VisionWorker: acquired after {swLock.ElapsedMilliseconds}ms (photo {photoId})");
				using IServiceScope scope = _scopeFactory.CreateScope();
				IImportService requiredService = scope.ServiceProvider.GetRequiredService<IImportService>();
				IMediaFileRepository repo = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
				await requiredService.RunVisionForPhotoAsync(photoId, ct);
				MediaFile updated = await ((IRepository<MediaFile>)(object)repo).GetByIdAsync(photoId);
				if (updated == null)
				{
					continue;
				}
				await ((DispatcherObject)Application.Current).Dispatcher.InvokeAsync((Action)delegate
				{
					MediaFile val = MediaFiles.FirstOrDefault((MediaFile m) => m.Id == photoId);
					if (val != null)
					{
						MediaFile? selectedMediaFile = SelectedMediaFile;
						bool num = selectedMediaFile != null && selectedMediaFile.Id == photoId;
						RefreshItemInPlace(updated, val);
						if (num)
						{
							RefreshDescriptionProperties();
						}
					}
					else if (MatchesCurrentView(updated))
					{
						MediaFiles.Add(updated);
						LoadSingleThumbnailAsync(updated);
					}
					PendingDescriptionCount = MediaFiles.Count((MediaFile m) => (int)m.AnalysisStatus == 4);
				}, (DispatcherPriority)2);
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				break;
			}
			catch (Exception ex3)
			{
				string text = ex3.Message;
				for (Exception innerException = ex3.InnerException; innerException != null; innerException = innerException.InnerException)
				{
					text = text + " -> " + innerException.Message;
				}
				AppLog.Error($"Vision worker exception for photo {photoId}: {ex3.GetType().Name}: {text}\n{ex3.StackTrace}");
			}
			finally
			{
				if (lockAcquired)
				{
					AppLog.Info($"[LOCK] VisionWorker: releasing _analysisLock (photo {photoId})");
					_analysisLock.Release();
				}
			}
		}
	}

	private async Task FaceDetectionWorkerAsync(CancellationToken ct)
	{
		using (IServiceScope serviceScope = _scopeFactory.CreateScope())
		{
			if (!serviceScope.ServiceProvider.GetRequiredService<IFaceService>().IsAvailable)
			{
				return;
			}
		}
		while (!ct.IsCancellationRequested)
		{
			try
			{
				IReadOnlyList<Guid> readOnlyList;
				using (IServiceScope scope = _scopeFactory.CreateScope())
				{
					readOnlyList = await scope.ServiceProvider.GetRequiredService<IMediaFileRepository>().GetFaceDetectionPendingIdsAsync(10);
				}
				if (readOnlyList.Count == 0)
				{
					await Task.Delay(TimeSpan.FromSeconds(30.0), ct);
					continue;
				}
				foreach (Guid photoId in readOnlyList)
				{
					if (ct.IsCancellationRequested)
					{
						return;
					}
					try
					{
						using IServiceScope scope = _scopeFactory.CreateScope();
						IMediaFileRepository repo = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
						IFaceService faceService = scope.ServiceProvider.GetRequiredService<IFaceService>();
						MediaFile mf = await ((IRepository<MediaFile>)(object)repo).GetByIdAsync(photoId);
						if (mf == null)
						{
							continue;
						}
						await faceService.DetectFacesAsync(mf, ct);
						await ((IRepository<MediaFile>)(object)repo).UpdateAsync(mf);
						goto IL_04a1;
					}
					catch (OperationCanceledException)
					{
						return;
					}
					catch (Exception ex2)
					{
						AppLog.Error($"FaceWorker: error on photo {photoId}: {ex2.GetType().Name}: {ex2.Message}");
						goto IL_04a1;
					}
					IL_04a1:
					await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
				}
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception ex4)
			{
				AppLog.Error("FaceWorker batch error: " + ex4.GetType().Name + ": " + ex4.Message);
				await Task.Delay(TimeSpan.FromSeconds(10.0), ct);
			}
		}
	}

	private void ShowAllPhotos()
	{
		PendingSearchQuery = "";
		SearchQuery = "";
		IsRelatedImagesMode = false;
		RelatedAnchorPhoto = null;
		_relatedPhotoIds.Clear();
		IsSimilaritySearch = false;
		_similaritySourceId = null;
		SimilaritySourceName = "";
		ActiveAlbumId = null;
		ActiveAlbumName = "";
		bool num = ActiveView == GalleryView.AllPhotos;
		ActiveView = GalleryView.AllPhotos;
		SetSidebarActiveAlbum(null);
		if (num)
		{
			LoadAsync();
		}
	}

	private void ShowFavorites()
	{
		PendingSearchQuery = "";
		SearchQuery = "";
		IsRelatedImagesMode = false;
		RelatedAnchorPhoto = null;
		_relatedPhotoIds.Clear();
		ActiveView = GalleryView.Favorites;
	}

	private async Task ShowOnThisDayAsync()
	{
		PendingSearchQuery = "";
		SearchQuery = "";
		IsRelatedImagesMode = false;
		RelatedAnchorPhoto = null;
		_relatedPhotoIds.Clear();
		ActiveView = GalleryView.OnThisDay;
		DateTime today = DateTime.Now;
		IReadOnlyList<MediaFile> allPhotos = await WithRepo((IMediaFileRepository repo) => repo.GetOnThisDayAsync(today));
		await OnThisDay.LoadAsync(allPhotos, today);
		OnThisDay.PhotoSelected -= OnMemoryPhotoSelected;
		OnThisDay.PhotoSelected += OnMemoryPhotoSelected;
		OnThisDay.SaveGroupAsAlbumRequested -= OnSaveOnThisDayGroupAsAlbum;
		OnThisDay.SaveGroupAsAlbumRequested += OnSaveOnThisDayGroupAsAlbum;
	}

	private async void OnSaveOnThisDayGroupAsAlbum(OnThisDayGroup group)
	{
		await SaveOnThisDayGroupAsAlbumAsync(group);
	}

	private void OnMemoryPhotoSelected(MediaFile photo)
	{
		PendingSearchQuery = "";
		SearchQuery = "";
		ActiveView = GalleryView.AllPhotos;
		SelectedMediaFile = MediaFiles.FirstOrDefault((MediaFile m) => m.Id == photo.Id);
		if (SelectedMediaFile != null)
		{
			this.ScrollIntoViewRequested?.Invoke(SelectedMediaFile);
		}
	}

	private void ShowOnThisDayPhoto(MediaFile photo)
	{
		OnMemoryPhotoSelected(photo);
	}

	private async Task SaveOnThisDayGroupAsAlbumAsync(OnThisDayGroup? group)
	{
		if (group == null || group.Photos.Count == 0)
		{
			return;
		}
		IReadOnlyList<Library> libraries = await WithLibRepo((ILibraryRepository r) => r.GetAllAsync());
		if (libraries.Count != 0)
		{
			string suggestedName = $"On This Day — {group.Year}";
			Album album = await WithLibRepo((ILibraryRepository r) => r.CreateAlbumAsync(libraries[0].Id, suggestedName, (string)null));
			List<Guid> photoIds = group.Photos.Select((MediaFile p) => p.Id).ToList();
			await WithLibRepo((ILibraryRepository r) => r.AddPhotosToAlbumAsync(album.Id, (IReadOnlyList<Guid>)photoIds));
			await LoadSidebarLibrariesAsync();
			NavigateToAlbum(album.Id, suggestedName);
			StatusText = $"Created album \"{suggestedName}\" with {photoIds.Count} photos";
		}
	}

	private void ShowTags()
	{
		using IServiceScope serviceScope = _scopeFactory.CreateScope();
		TagsBrowserWindow window = serviceScope.ServiceProvider.GetRequiredService<TagsBrowserWindow>();
		window.Owner = Application.Current.MainWindow;
		((TagsBrowserViewModel)window.DataContext).RequestFilterByTag += delegate(string tagName)
		{
			window.Close();
			SearchQuery = tagName;
			ActiveView = GalleryView.AllPhotos;
		};
		window.ShowDialog();
	}

	public void NavigateToAlbum(Guid albumId, string albumName)
	{
		PendingSearchQuery = "";
		SearchQuery = "";
		IsRelatedImagesMode = false;
		RelatedAnchorPhoto = null;
		_relatedPhotoIds.Clear();
		ActiveAlbumId = albumId;
		ActiveAlbumName = albumName;
		ActiveView = GalleryView.Album;
		SetSidebarActiveAlbum(albumId);
	}

	private void SetSidebarActiveAlbum(Guid? albumId)
	{
		foreach (LibraryNodeViewModel sidebarLibrary in SidebarLibraries)
		{
			sidebarLibrary.IsActive = false;
			foreach (LibraryNodeViewModel child in sidebarLibrary.Children)
			{
				child.IsActive = child.Id == albumId;
			}
		}
	}

	private void NavigateToAlbumSidebar(LibraryNodeViewModel? node)
	{
		if (node != null && !node.IsLibrary)
		{
			NavigateToAlbum(node.Id, node.Name);
		}
	}

	private void ShowPeople()
	{
		using IServiceScope serviceScope = _scopeFactory.CreateScope();
		PeopleWindow window = serviceScope.ServiceProvider.GetRequiredService<PeopleWindow>();
		window.Owner = Application.Current.MainWindow;
		((PeopleViewModel)window.DataContext).RequestFilterByPerson += async delegate(Guid personId)
		{
			window.Close();
			await FilterByPersonAsync(personId);
		};
		window.ShowDialog();
	}

	private async Task FilterByPersonAsync(Guid personId)
	{
		IReadOnlyList<Guid> photoIds = await WithRepo((IMediaFileRepository repo) => repo.GetFacePhotoIdsForPersonAsync(personId));
		IReadOnlyList<MediaFile> readOnlyList = await WithRepo((IMediaFileRepository repo) => repo.GetByIdsAsync((IEnumerable<Guid>)photoIds));
		_personFilterPhotoIds = photoIds;
		MediaFiles = new ObservableCollection<MediaFile>(readOnlyList);
		GalleryCollectionReplaced?.Invoke();
		StatusText = $"{readOnlyList.Count} photo(s) featuring this person";
		IsPersonFilterActive = true;
		ActiveFilterLabel = $"Showing {readOnlyList.Count} photo{((readOnlyList.Count == 1) ? "" : "s")} with this person";
		ActiveView = GalleryView.AllPhotos;
	}

	private async Task ManageLibrariesAsync()
	{
		using IServiceScope scope = _scopeFactory.CreateScope();
		ManageLibrariesWindow requiredService = scope.ServiceProvider.GetRequiredService<ManageLibrariesWindow>();
		requiredService.Owner = Application.Current.MainWindow;
		requiredService.ShowDialog();
		if (requiredService.Tag is ITuple { Length: 2 } tuple && tuple[0] is Guid albumId && tuple[1] is string albumName)
		{
			NavigateToAlbum(albumId, albumName);
		}
		await LoadSidebarLibrariesAsync();
	}

	private async Task RemoveFromAlbumAsync()
	{
		if (!IsAlbumActive || !ActiveAlbumId.HasValue)
		{
			return;
		}
		List<MediaFile> targets = (HasMultiSelection ? MultiSelectedItems.ToList() : ((SelectedMediaFile != null) ? new List<MediaFile>(1) { SelectedMediaFile } : new List<MediaFile>()));
		if (targets.Count == 0)
		{
			return;
		}
		List<Guid> photoIds = targets.Select((MediaFile m) => m.Id).ToList();
		await WithLibRepo((ILibraryRepository r) => r.RemovePhotosFromAlbumAsync(ActiveAlbumId.Value, (IReadOnlyList<Guid>)photoIds));
		foreach (MediaFile item in targets)
		{
			MediaFiles.Remove(item);
		}
		PhotoCount = MediaFiles.Count;
		StatusText = $"Removed {targets.Count} photo{((targets.Count == 1) ? "" : "s")} from \"{ActiveAlbumName}\"";
		await LoadSidebarLibrariesAsync();
	}

	private async Task AddToAlbumAsync()
	{
		List<MediaFile> targets = (HasMultiSelection ? MultiSelectedItems.ToList() : ((SelectedMediaFile != null) ? new List<MediaFile>(1) { SelectedMediaFile } : new List<MediaFile>()));
		if (targets.Count == 0)
		{
			return;
		}
		AlbumPickerWindow picker = new AlbumPickerWindow(await WithLibRepo((ILibraryRepository r) => r.GetAllAsync()))
		{
			Owner = Application.Current.MainWindow
		};
		if (picker.ShowDialog() != true)
		{
			return;
		}
		List<Guid> photoIds = targets.Select((MediaFile m) => m.Id).ToList();
		Guid albumId;
		string albumLabel;
		if (picker.IsNewAlbum)
		{
			Guid libraryId;
			if (picker.NewAlbumLibraryId.HasValue)
			{
				libraryId = picker.NewAlbumLibraryId.Value;
			}
			else
			{
				libraryId = (await WithLibRepo((ILibraryRepository r) => r.CreateLibraryAsync(picker.NewLibraryName, (string)null))).Id;
			}
			Album val = await WithLibRepo((ILibraryRepository r) => r.CreateAlbumAsync(libraryId, picker.NewAlbumName, (string)null));
			albumId = val.Id;
			albumLabel = picker.NewAlbumName;
		}
		else
		{
			if (!picker.SelectedAlbumId.HasValue)
			{
				return;
			}
			albumId = picker.SelectedAlbumId.Value;
			albumLabel = picker.SelectedAlbumName;
		}
		await WithLibRepo((ILibraryRepository r) => r.AddPhotosToAlbumAsync(albumId, (IReadOnlyList<Guid>)photoIds));
		StatusText = $"Added {targets.Count} photo{((targets.Count == 1) ? "" : "s")} to \"{albumLabel}\"";
		await LoadSidebarLibrariesAsync();
	}

	public async Task DropPhotosOnAlbumAsync(Guid albumId, string albumName, IEnumerable<Guid> photoIds)
	{
		List<Guid> ids = photoIds.ToList();
		if (ids.Count != 0)
		{
			await WithLibRepo((ILibraryRepository r) => r.AddPhotosToAlbumAsync(albumId, (IReadOnlyList<Guid>)ids));
			StatusText = $"Added {ids.Count} photo{((ids.Count == 1) ? "" : "s")} to \"{albumName}\"";
			await LoadSidebarLibrariesAsync();
		}
	}

	public async Task DropPhotosOnLibraryAsync(Guid libraryId, string libraryName, IEnumerable<Guid> photoIds)
	{
		List<Guid> ids = photoIds.ToList();
		if (ids.Count == 0)
		{
			return;
		}
		List<LibraryNodeViewModel> list = SidebarLibraries.FirstOrDefault((LibraryNodeViewModel n) => n.Id == libraryId)?.Children.ToList() ?? new List<LibraryNodeViewModel>();
		Guid albumId;
		string albumLabel;
		if (list.Count == 0)
		{
			albumId = (await WithLibRepo((ILibraryRepository r) => r.CreateAlbumAsync(libraryId, libraryName, (string)null))).Id;
			albumLabel = libraryName;
		}
		else if (list.Count == 1)
		{
			albumId = list[0].Id;
			albumLabel = list[0].Name;
		}
		else
		{
			albumId = list[0].Id;
			albumLabel = list[0].Name;
		}
		await WithLibRepo((ILibraryRepository r) => r.AddPhotosToAlbumAsync(albumId, (IReadOnlyList<Guid>)ids));
		StatusText = $"Added {ids.Count} photo{((ids.Count == 1) ? "" : "s")} to \"{albumLabel}\"";
		await LoadSidebarLibrariesAsync();
	}

	private async Task LoadSidebarLibrariesAsync()
	{
		Dictionary<Guid, int> counts = await WithLibRepo((ILibraryRepository r) => r.GetAlbumPhotoCountsAsync());
		IReadOnlyList<Library> obj = await WithLibRepo((ILibraryRepository r) => r.GetAllAsync());
		SidebarLibraries.Clear();
		foreach (Library item in obj)
		{
			LibraryNodeViewModel libraryNodeViewModel = new LibraryNodeViewModel(item);
			foreach (Album item2 in item.Albums.OrderBy((Album a) => a.Name))
			{
				libraryNodeViewModel.Children.Add(new LibraryNodeViewModel(item2)
				{
					PhotoCount = (counts.TryGetValue(item2.Id, out var value) ? value : 0)
				});
			}
			SidebarLibraries.Add(libraryNodeViewModel);
		}
	}

	private async Task LoadExclusionCacheAsync()
	{
		using IServiceScope scope = _scopeFactory.CreateScope();
		List<ExclusionRule> obj = await scope.ServiceProvider.GetRequiredService<IExclusionRepository>().GetAllAsync();
		_excludedFullPaths.Clear();
		_excludedNames.Clear();
		foreach (ExclusionRule item in obj)
		{
			if (item.IsFullPath)
			{
				_excludedFullPaths.Add(item.Value);
			}
			else
			{
				_excludedNames.Add(item.Value);
			}
		}
		OnPropertyChanged("SelectedFolderIsExcluded");
	}

	private async Task ImportAsync()
	{
		if (!Progress.IsRunning && !(await CheckExpressCeilingAsync()))
		{
			OpenFolderDialog openFolderDialog = new OpenFolderDialog
			{
				Title = "Select folders to import",
				Multiselect = true
			};
			bool? flag;
			try
			{
				Window activeWindow = GetActiveWindow();
				flag = openFolderDialog.ShowDialog(activeWindow);
			}
			catch (Exception ex)
			{
				StatusText = "Failed to open folder dialog: " + ex.Message;
				AppLog.Error($"ImportAsync dialog error: {ex}");
				return;
			}
			if (flag == true && openFolderDialog.FolderNames.Length != 0)
			{
				AttachTaskbar(Progress);
				AttachPhotoCallback(Progress);
				AttachExpressLimitCallback(Progress);
				StatusText = "";
				await Progress.RunImportSelectionAsync(openFolderDialog.FolderNames, Array.Empty<string>());
				ClearTaskbar();
				StatusText = Progress.ResultMessage;
				_folderWatcher.RefreshFoldersAsync(default(CancellationToken));
				await LoadAsync();
			}
		}
	}

	private async Task ImportFolderFromShellAsync((string folder, bool recursive) args)
	{
		if (!(await CheckExpressCeilingAsync()))
		{
			AttachTaskbar(Progress);
			AttachPhotoCallback(Progress);
			AttachExpressLimitCallback(Progress);
			StatusText = "";
			await Progress.RunMultiFolderAsync([args.folder], args.recursive);
			ClearTaskbar();
			StatusText = Progress.ResultMessage;
			_folderWatcher.RefreshFoldersAsync(default(CancellationToken));
			await LoadAsync();
		}
	}

	private async Task RemoveFolderFromShellAsync((string folder, bool recursive) args)
	{
		string fileName = Path.GetFileName(args.folder.TrimEnd('\\', '/'));
		string text = (args.recursive ? ("\"" + fileName + "\" and all subfolders") : ("\"" + fileName + "\" (this folder only, not subfolders)"));
		if (MessageBox.Show("Remove all photos in " + text + " from PhotoWell?\n\nThis removes them from the library but does not delete any files.", "Remove from Library", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
		{
			return;
		}
		using IServiceScope scope = _scopeFactory.CreateScope();
		int num = await scope.ServiceProvider.GetRequiredService<IMediaFileRepository>().RemoveByFolderAsync(args.folder, args.recursive);
		StatusText = $"{num} photo{((num == 1) ? "" : "s")} removed from library.";
		await LoadAsync();
	}

	private async Task ImportFilesAsync()
	{
		if (!Progress.IsRunning && !(await CheckExpressCeilingAsync()))
		{
			string photoFilterSpec = SupportedFormats.PhotoFilterSpec;
			string rawFilterSpec = SupportedFormats.RawFilterSpec;
			OpenFileDialog openFileDialog = new OpenFileDialog
			{
				Title = "Select photos to import",
				Multiselect = true,
				Filter = $"All Photos ({photoFilterSpec};{rawFilterSpec})|{photoFilterSpec};{rawFilterSpec}|Photos ({photoFilterSpec})|{photoFilterSpec}|RAW Files ({rawFilterSpec})|{rawFilterSpec}"
			};
			bool? flag;
			try
			{
				Window activeWindow = GetActiveWindow();
				flag = openFileDialog.ShowDialog(activeWindow);
			}
			catch (Exception ex)
			{
				StatusText = "Failed to open file dialog: " + ex.Message;
				AppLog.Error($"ImportFilesAsync dialog error: {ex}");
				return;
			}
			if (flag == true && openFileDialog.FileNames.Length != 0)
			{
				AttachTaskbar(Progress);
				AttachPhotoCallback(Progress);
				AttachExpressLimitCallback(Progress);
				StatusText = "";
				await Progress.RunImportSelectionAsync(Array.Empty<string>(), openFileDialog.FileNames);
				ClearTaskbar();
				StatusText = Progress.ResultMessage;
				_folderWatcher.RefreshFoldersAsync(default(CancellationToken));
				await LoadAsync();
			}
		}
	}

	private void BeginEditDescription()
	{
		if (SelectedMediaFile != null)
		{
			EditingDescriptionText = DisplayDescription;
			IsEditingDescription = true;
		}
	}

	private async Task SaveDescriptionAsync()
	{
		if (SelectedMediaFile == null)
		{
			return;
		}
		string text = EditingDescriptionText.Trim();
		string newDescription = (string.IsNullOrEmpty(text) ? null : text);
		Guid id = SelectedMediaFile.Id;
		using (IServiceScope scope = _scopeFactory.CreateScope())
		{
			IMediaFileRepository freshRepo = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
			MediaFile val = await ((IRepository<MediaFile>)(object)freshRepo).GetByIdAsync(id);
			if (val != null)
			{
				val.UserDescription = newDescription;
				val.DescriptionEditedAt = DateTime.UtcNow;
				await ((IRepository<MediaFile>)(object)freshRepo).UpdateAsync(val);
			}
		}
		SelectedMediaFile.UserDescription = newDescription;
		SelectedMediaFile.DescriptionEditedAt = DateTime.UtcNow;
		IsEditingDescription = false;
	}

	private void CancelEditDescription()
	{
		IsEditingDescription = false;
	}

	private void BeginEditCaption()
	{
		if (SelectedMediaFile != null)
		{
			EditingCaptionText = DisplayCaption;
			IsEditingCaption = true;
		}
	}

	private async Task SaveCaptionAsync()
	{
		if (SelectedMediaFile == null)
		{
			return;
		}
		string raw = EditingCaptionText;
		string text = raw.Trim();
		string caption = (string.IsNullOrEmpty(text) ? null : text);
		Guid id = SelectedMediaFile.Id;
		using (IServiceScope scope = _scopeFactory.CreateScope())
		{
			IMediaFileRepository freshRepo = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
			MediaFile val = await ((IRepository<MediaFile>)(object)freshRepo).GetByIdAsync(id);
			if (val != null)
			{
				val.UserCaptionRaw = (string.IsNullOrEmpty(raw) ? null : raw);
				val.UserCaption = caption;
				val.CaptionEditedAt = DateTime.UtcNow;
				await ((IRepository<MediaFile>)(object)freshRepo).UpdateAsync(val);
			}
		}
		SelectedMediaFile.UserCaptionRaw = (string.IsNullOrEmpty(raw) ? null : raw);
		SelectedMediaFile.UserCaption = caption;
		SelectedMediaFile.CaptionEditedAt = DateTime.UtcNow;
		IsEditingCaption = false;
	}

	private void CancelEditCaption()
	{
		IsEditingCaption = false;
	}

	private bool CanRunReanalyze()
	{
		return !SelectedIsOffline;
	}

	/// <summary>
	/// Runs a single-photo reanalysis in the background without touching ImportProgressViewModel state.
	/// Called during an active queue import so the import continues uninterrupted.
	/// Multiple calls serialize naturally behind <see cref="_analysisLock"/>.
	/// </summary>
	private async Task ReanalyzeOneDuringImportAsync(Guid photoId, string fileName, MediaFile priorItem)
	{
		try
		{
			AppLog.Info($"[PRIORITY] ReanalyzeOneDuringImportAsync queued: {fileName}");
			using (await AcquireAnalysisLockAsync("ReanalyzeOne-DuringImport"))
			{
				using var scope = _scopeFactory.CreateScope();
				var importSvc = scope.ServiceProvider.GetRequiredService<IImportService>();
				await importSvc.ReanalyzeFileAsync(photoId, ct: CancellationToken.None);
			}
			using IServiceScope readScope = _scopeFactory.CreateScope();
			var updated = await ((IRepository<MediaFile>)(object)readScope.ServiceProvider.GetRequiredService<IMediaFileRepository>()).GetByIdAsync(photoId);
			if (updated != null)
			{
				Application.Current.Dispatcher.Invoke(() =>
				{
					// Find by ID — import may have swapped the original reference out.
					int idx = -1;
					for (int i = 0; i < MediaFiles.Count; i++)
					{
						if (MediaFiles[i].Id == photoId) { idx = i; break; }
					}
					if (idx >= 0)
					{
						// Capture wasSelected BEFORE the replace. MediaFiles[idx] = updated
						// fires a WPF CollectionChanged(Replace) which causes the ListBox to
						// clear SelectedItem (old reference gone), pushing null back through
						// the two-way binding. Reading SelectedMediaFile after the replace
						// would see null and skip the re-select, blanking the detail pane.
						bool wasSelected = SelectedMediaFile?.Id == photoId;
						updated.LoadedThumbnail = MediaFiles[idx].LoadedThumbnail;
						MediaFiles[idx] = updated;
						if (wasSelected)
						{
							SelectedMediaFile = updated;
							ScrollIntoViewRequested?.Invoke(updated);
						}
						_activeViewerVm?.PhotoReanalyzed?.Invoke(updated);
					}
					StatusText = $"Re-analyzed \"{fileName}\"";
				});
			}
			AppLog.Info($"[PRIORITY] ReanalyzeOneDuringImportAsync complete: {fileName}");
			await RefreshAfterBulkReanalysisAsync();
		}
		catch (Exception ex)
		{
			AppLog.Error($"[PRIORITY] ReanalyzeOneDuringImportAsync failed for {fileName}: {ex.GetType().Name}: {ex.Message}");
			Application.Current.Dispatcher.Invoke(() => StatusText = $"Re-analysis failed: {ex.Message}");
		}
	}

	private bool CanRunBulkReanalyze()
	{
		return !Progress.IsRunning;
	}

	private async Task ReanalyzeSelectedAsync()
	{
		if (SelectedMediaFile == null)
		{
			return;
		}
		if (SelectedMediaFile.IsVisionPending)
		{
			PrioritizeVision(SelectedMediaFile.Id);
			StatusText = "Moved \"" + SelectedMediaFile.FileName + "\" to front of analysis queue";
		}
		else if (Progress.IsRunning && Progress.IsResumable)
		{
			Progress.EnqueuePriorityReanalysis(SelectedMediaFile.Id);
			StatusText = "Moved \"" + SelectedMediaFile.FileName + "\" to front of update queue";
		}
		else if (Progress.IsRunning && Progress.IsQueueImport)
		{
			// User has priority — reanalyze without touching the import's state.
			// Fire-and-forget so AsyncRelayCommand re-enables the button immediately,
			// allowing the user to queue additional photos. Each click serializes behind
			// the analysis lock, so multiple requests process one at a time after Ollama.
			if (SelectedMediaFile.UserDescription != null && MessageBox.Show("You've edited the AI description for \"" + SelectedMediaFile.FileName + "\".\n\nRe-analyzing will replace your custom description with a new AI-generated one.\n\nContinue?", "Overwrite Your Edit?", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) != MessageBoxResult.Yes)
				return;

			Guid photoId = SelectedMediaFile.Id;
			string fileName = SelectedMediaFile.FileName;
			MediaFile priorItem = SelectedMediaFile;
			StatusText = $"Re-analysis of \"{fileName}\" queued — will run after current AI call";
			_ = ReanalyzeOneDuringImportAsync(photoId, fileName, priorItem);
		}
		else if (Progress.IsRunning)
		{
			StatusText = "Analysis is already running — wait for it to finish or cancel it first";
		}
		else
		{
			if (SelectedMediaFile.UserDescription != null && MessageBox.Show("You've edited the AI description for \"" + SelectedMediaFile.FileName + "\".\n\nRe-analyzing will replace your custom description with a new AI-generated one.\n\nContinue?", "Overwrite Your Edit?", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) != MessageBoxResult.Yes)
			{
				return;
			}
			AttachTaskbar(Progress);
			Guid photoId = SelectedMediaFile.Id;
			string fileName = SelectedMediaFile.FileName;
			MediaFile priorItem = SelectedMediaFile;
			using (await AcquireAnalysisLockAsync("ReanalyzeOne"))
			{
				await Progress.RunReanalyzeOneAsync(photoId, fileName);
			}
			ClearTaskbar();
			using IServiceScope scope = _scopeFactory.CreateScope();
			MediaFile val = await ((IRepository<MediaFile>)(object)scope.ServiceProvider.GetRequiredService<IMediaFileRepository>()).GetByIdAsync(photoId);
			if (val != null)
			{
				RefreshItemInPlace(val, priorItem);
				if (SelectedMediaFile?.Id == photoId)
					this.ScrollIntoViewRequested?.Invoke(val);
				_activeViewerVm?.PhotoReanalyzed?.Invoke(val);
			}
			StatusText = Progress.ResultMessage;
			await RefreshAfterBulkReanalysisAsync();
		}
	}

	private async Task ReanalyzeAllAsync()
	{
		if (Progress.IsRunning)
		{
			if (MessageBox.Show("\"" + Progress.Heading + "\" is currently running.\n\nStop it and re-analyze all photos instead?", "Re-analyze All", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
			{
				return;
			}
			Progress.ForceCancel();
			while (Progress.IsRunning)
			{
				await Task.Delay(100);
			}
		}
		int value = await WithRepo((IMediaFileRepository r) => ((IRepository<MediaFile>)(object)r).CountAsync());
		if (MessageBox.Show($"Re-analyze all {value} photos with AI?\n\nThis will replace existing tags and descriptions. It may take a while.", "Re-analyze All", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
		{
			bool skipUserModified = false;
			int num = MediaFiles.Count((MediaFile m) => m.UserDescription != null);
			if (num > 0)
			{
				MessageBoxResult messageBoxResult = MessageBox.Show($"{num} photo{((num == 1) ? " has" : "s have")} a user-edited description.\n\n" + "Re-analyze those too?\n\n• Yes — re-analyze all photos (user edits will be overwritten)\n• No  — skip photos with user-edited descriptions", "User-Edited Descriptions", MessageBoxButton.YesNo, MessageBoxImage.Question);
				skipUserModified = messageBoxResult == MessageBoxResult.No;
			}
			AttachTaskbar(Progress);
			AttachMetrics(Progress);
			AttachPhotoCallback(Progress);
			using (await AcquireAnalysisLockAsync("ReanalyzeAll"))
			{
				await Progress.RunReanalyzeAsync(unanalyzedOnly: false, skipUserModified);
			}
			ClearTaskbar();
			Metrics.ClearBatchStatus();
			StatusText = Progress.ResultMessage;
			await RefreshAfterBulkReanalysisAsync();
		}
	}

	private void ToggleSelection(MediaFile? file)
	{
		if (file != null)
		{
			if (MultiSelectedItems.Contains(file))
			{
				MultiSelectedItems.Remove(file);
			}
			else
			{
				MultiSelectedItems.Add(file);
				_selectionAnchor = file;
			}
			NotifyMultiSelectChanged();
		}
	}

	private void SelectRange(MediaFile? target)
	{
		if (target == null)
		{
			return;
		}
		if (_selectionAnchor == null)
		{
			ToggleSelection(target);
			return;
		}
		int num = MediaFiles.IndexOf(_selectionAnchor);
		int num2 = MediaFiles.IndexOf(target);
		if (num < 0 || num2 < 0)
		{
			ToggleSelection(target);
			return;
		}
		int num3 = Math.Min(num, num2);
		int num4 = Math.Max(num, num2);
		for (int i = num3; i <= num4; i++)
		{
			if (!MultiSelectedItems.Contains(MediaFiles[i]))
			{
				MultiSelectedItems.Add(MediaFiles[i]);
			}
		}
		NotifyMultiSelectChanged();
	}

	private void SelectAll()
	{
		foreach (MediaFile mediaFile in MediaFiles)
		{
			if (!MultiSelectedItems.Contains(mediaFile))
			{
				MultiSelectedItems.Add(mediaFile);
			}
		}
		_selectionAnchor = MediaFiles.LastOrDefault();
		NotifyMultiSelectChanged();
	}

	private void ClearSelection()
	{
		MultiSelectedItems.Clear();
		_selectionAnchor = null;
		NotifyMultiSelectChanged();
	}

	private async Task ExecuteSearchAsync()
	{
		SearchQuery = PendingSearchQuery;
		await LoadAsync();
	}

	private async Task SearchByTagAsync(string tag)
	{
		PendingSearchQuery = tag;
		SearchQuery = tag;
		await LoadAsync();
	}

	private async Task ClearSearchAsync()
	{
		PendingSearchQuery = "";
		SearchQuery = "";
		_similaritySourceId = null;
		IsSimilaritySearch = false;
		SimilaritySourceName = "";
		await LoadAsync();
	}

	private void NotifyMultiSelectChanged()
	{
		MultiSelectedVersion++;
		OnPropertyChanged("MultiSelectedVersion");
		OnPropertyChanged("SelectedCount");
		OnPropertyChanged("HasAnyMultiSelected");
		OnPropertyChanged("HasMultiSelection");
		OnPropertyChanged("HasAnySelected");
		OnPropertyChanged("ReanalyzeMultiSelectedLabel");
		OnPropertyChanged("ExcludeImageLabel");
		OnPropertyChanged("RemoveFromLibraryLabel");
		OnPropertyChanged("DeletePermanentlyLabel");
		((IRelayCommand)ReanalyzeMultiSelectedCommand).NotifyCanExecuteChanged();
	}

	/// <summary>
	/// Called from App.xaml.cs after onboarding/tip-toast to start the 2-second
	/// resume-import countdown. Kept internal to the ViewModel layer.
	/// </summary>
	public void ScheduleResumeImportQueue()
	{
		Task.Delay(TimeSpan.FromSeconds(2.0))
		    .ContinueWith((Task _) => ResumeImportQueueAsync(), TaskScheduler.Default)
		    .Unwrap();
	}

	private async Task ResumeImportQueueAsync()
	{
		AppLog.Info("[RESUME] ResumeImportQueueAsync entry");
		try
		{
		int count = await Progress.GetQueueCountAsync();
		AppLog.Info($"[RESUME] Queue count = {count}");
		if (count == 0)
		{
			return;
		}
		var result = ((DispatcherObject)Application.Current).Dispatcher.Invoke<MessageBoxResult>((Func<MessageBoxResult>)(() =>
			MessageBox.Show(Application.Current.MainWindow,
				$"{count} import folder{((count == 1) ? "" : "s")} remain from your last session.\n\nResume importing them now?",
				"Resume Import", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes)));
		AppLog.Info($"[RESUME] Dialog result = {result}");
		if (result != MessageBoxResult.Yes)
		{
			await Progress.ClearQueueAsync();
			return;
		}
		StatusText = $"Resuming import of {count:N0} folder{(count == 1 ? "" : "s")}…";

		// Cancel any in-progress background operation unconditionally — the user chose to resume,
		// so this import takes priority. ForceCancel is a no-op if nothing is running.
		AppLog.Info($"[RESUME] User confirmed resume. IsRunning={Progress.IsRunning}. Calling ForceCancel.");
		Progress.ForceCancel();
		if (Progress.IsRunning)
		{
			// Wait up to 30 s for the operation to honour the cancellation.
			// 5 s was too short: AI analysis of a large RAW file can take longer to cancel.
			for (int i = 0; i < 300 && Progress.IsRunning; i++)
				await Task.Delay(100);
			if (Progress.IsRunning)
				AppLog.Error("[RESUME] Operation still running after 30 s wait — proceeding with force-start.");
		}

		AttachTaskbar(Progress);
		AttachPhotoCallback(Progress);
		AttachExpressLimitCallback(Progress);
		// No analysis lock here — the import itself doesn't call Ollama; only the vision
		// background worker does (with its own per-photo lock acquisition). Holding
		// _analysisLock for the full import (potentially hours) would permanently starve
		// any user-initiated single-photo reanalysis.
		await Progress.RunFromQueueAsync(forceStart: true);
		ClearTaskbar();
		StatusText = Progress.ResultMessage;
		await LoadAsync();
		Task.Run(async delegate
		{
			try
			{
				using IServiceScope scope = _scopeFactory.CreateScope();
				await scope.ServiceProvider.GetRequiredService<IMediaFileRepository>().RebuildFtsIndexAsync();
			}
			catch (Exception ex)
			{
				AppLog.Error("Post-resume FTS rebuild failed: " + ex.Message);
			}
		});
		}
		catch (Exception ex)
		{
			AppLog.Error($"[RESUME] ResumeImportQueueAsync failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
		}
	}

	private async Task<bool> CheckExpressCeilingAsync()
	{
		if (!UserPreferences.Current.IsExpressMode)
		{
			return false;
		}
		int num = await WithRepo((IMediaFileRepository r) => ((IRepository<MediaFile>)(object)r).CountAsync());
		if (num < 25000)
		{
			return false;
		}
		UpgradePromptWindow upgradePromptWindow = new UpgradePromptWindow(num);
		upgradePromptWindow.Owner = Application.Current.MainWindow;
		upgradePromptWindow.ShowDialog();
		return true;
	}

	private static Window GetActiveWindow()
	{
		return Application.Current.Windows.OfType<Window>().FirstOrDefault((Window w) => w.IsActive) ?? Application.Current.MainWindow;
	}

	private void AttachTaskbar(ImportProgressViewModel vm)
	{
		StatusText = "";
		vm.TaskbarProgress = delegate(double v)
		{
			TaskbarProgressValue = v;
			TaskbarProgressState = TaskbarItemProgressState.Normal;
		};
	}

	private void AttachMetrics(ImportProgressViewModel vm)
	{
		vm.OnBatchTick = delegate(string text)
		{
			Metrics.BatchStatusText = text;
		};
	}

	private void RefreshItemInPlace(MediaFile target, MediaFile? source = null, bool reloadThumbnail = false)
	{
		int num = MediaFiles.IndexOf(source ?? target);
		if (num >= 0)
		{
			if (source != null && !reloadThumbnail)
			{
				target.LoadedThumbnail = source.LoadedThumbnail;
			}
			bool wasSelected = SelectedMediaFile?.Id == target.Id;
			MediaFiles[num] = target;
			if (wasSelected)
				SelectedMediaFile = target;
			if (reloadThumbnail)
			{
				LoadSingleThumbnailAsync(target);
			}
		}
	}

	private async Task RefreshAfterBulkReanalysisAsync()
	{
		PendingDescriptionCount = MediaFiles.Count((MediaFile m) => (int)m.AnalysisStatus == 4);
		OutdatedDescriptionCount = await WithRepo((IMediaFileRepository r) => r.CountOutdatedDescriptionsAsync(AppSettings.VisionModelName, AppSettings.CurrentPromptVersion, AppSettings.CurrentPostProcessVersion));
		OnPropertyChanged("IsEmpty");
		OnPropertyChanged("IsLibraryEmpty");
		OnPropertyChanged("PhotoCountText");
		OnPropertyChanged("PhotoCountTooltip");
	}

	private void AttachPhotoCallback(ImportProgressViewModel vm)
	{
		vm.OnPhotoAnalyzed = delegate(MediaFile analyzed)
		{
			((DispatcherObject)Application.Current).Dispatcher.Invoke((Action)delegate
			{
				//IL_00c4: Unknown result type (might be due to invalid IL or missing references)
				//IL_00ca: Invalid comparison between Unknown and I4
				// Don't surface photos from folders the user just excluded —
				// they may have been queued before the exclusion took effect.
				var analyzedFolder = Path.GetDirectoryName(analyzed.FilePath) ?? "";
				if (_excludedFullPaths.Any(excl =>
					analyzedFolder.Equals(excl, StringComparison.OrdinalIgnoreCase) ||
					analyzedFolder.StartsWith(excl.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
				{
					return;
				}

				MediaFile val = MediaFiles.FirstOrDefault((MediaFile m) => m.Id == analyzed.Id);
				if (val != null)
				{
					RefreshItemInPlace(analyzed, val, reloadThumbnail: true);
				}
				else if (MatchesCurrentView(analyzed))
				{
					MediaFiles.Add(analyzed);
					PhotoCount = MediaFiles.Count;
					OnPropertyChanged("IsEmpty");
					OnPropertyChanged("IsLibraryEmpty");
					LoadSingleThumbnailAsync(analyzed);
				}
				if ((int)analyzed.AnalysisStatus == 4)
				{
					EnqueueForVision(analyzed.Id);
					PendingDescriptionCount++;
				}
			});
		};
	}

	private void AttachExpressLimitCallback(ImportProgressViewModel vm)
	{
		vm.OnExpressLimitReached = delegate
		{
			((DispatcherObject)Application.Current).Dispatcher.Invoke<Task>((Func<Task>)async delegate
			{
				int currentCount;
				using (IServiceScope scope = _scopeFactory.CreateScope())
				{
					currentCount = await ((IRepository<MediaFile>)(object)scope.ServiceProvider.GetRequiredService<IMediaFileRepository>()).CountAsync();
				}
				UpgradePromptWindow upgradePromptWindow = new UpgradePromptWindow(currentCount);
				upgradePromptWindow.Owner = Application.Current.MainWindow;
				upgradePromptWindow.ShowDialog();
			});
		};
	}

	private void ClearTaskbar()
	{
		TaskbarProgressState = TaskbarItemProgressState.None;
	}

	private async Task ReanalyzeMultiSelectedAsync()
	{
		List<MediaFile> items = MultiSelectedItems.ToList();
		if (items.Count == 0 || Progress.IsRunning)
		{
			return;
		}
		int num = items.Count((MediaFile m) => m.UserDescription != null);
		if (num <= 0 || MessageBox.Show($"{num} of the selected photo{((num == 1) ? "" : "s")} ha{((num == 1) ? "s" : "ve")} a user-edited description.\n\n" + "Re-analyzing will replace those edits with new AI-generated descriptions.\n\nContinue?", "Overwrite Your Edits?", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) == MessageBoxResult.Yes)
		{
			AttachTaskbar(Progress);
			using (await AcquireAnalysisLockAsync("ReanalyzeSelected"))
			{
				await Progress.RunReanalyzeSelectedAsync(items);
			}
			ClearTaskbar();
			StatusText = Progress.ResultMessage;
			await LoadAsync();
			ClearSelection();
		}
	}

	private async Task ReanalyzeOutdatedAsync()
	{
		if (!Progress.IsRunning)
		{
			bool skipUserModified = false;
			int num = MediaFiles.Count((MediaFile m) => m.UserDescription != null);
			if (num > 0)
			{
				MessageBoxResult messageBoxResult = MessageBox.Show($"{num} photo{((num == 1) ? " has" : "s have")} a user-edited description.\n\n" + "Update those too?\n\n• Yes — update all outdated photos (user edits will be overwritten)\n• No  — skip photos with user-edited descriptions", "User-Edited Descriptions", MessageBoxButton.YesNo, MessageBoxImage.Question);
				skipUserModified = messageBoxResult == MessageBoxResult.No;
			}
			AttachTaskbar(Progress);
			AttachMetrics(Progress);
			AttachPhotoCallback(Progress);
			using (await AcquireAnalysisLockAsync("ReanalyzeOutdated"))
			{
				await Progress.RunReanalyzeOutdatedAsync(skipUserModified);
			}
			ClearTaskbar();
			Metrics.ClearBatchStatus();
			StatusText = Progress.ResultMessage;
			await RefreshAfterBulkReanalysisAsync();
		}
	}

	private void NavigatePrevious()
	{
		if (SelectedMediaFile == null && MediaFiles.Count > 0)
		{
			SelectedMediaFile = MediaFiles[0];
			return;
		}
		int num = MediaFiles.IndexOf(SelectedMediaFile);
		if (num > 0)
		{
			SelectedMediaFile = MediaFiles[num - 1];
		}
	}

	private void NavigateNext()
	{
		if (SelectedMediaFile == null && MediaFiles.Count > 0)
		{
			SelectedMediaFile = MediaFiles[0];
			return;
		}
		int num = MediaFiles.IndexOf(SelectedMediaFile);
		if (num >= 0 && num < MediaFiles.Count - 1)
		{
			SelectedMediaFile = MediaFiles[num + 1];
		}
	}

	private void NavigateByOffset(int offset)
	{
		if (MediaFiles.Count == 0)
		{
			return;
		}
		if (SelectedMediaFile == null)
		{
			SelectedMediaFile = MediaFiles[0];
			return;
		}
		int num = MediaFiles.IndexOf(SelectedMediaFile);
		int num2 = Math.Clamp(num + offset, 0, MediaFiles.Count - 1);
		if (num2 != num)
		{
			SelectedMediaFile = MediaFiles[num2];
		}
	}

	private void OpenInExplorer()
	{
		MediaFile? selectedMediaFile = SelectedMediaFile;
		string text = ((selectedMediaFile != null) ? selectedMediaFile.FilePath : null);
		if (text != null && File.Exists(text))
		{
			Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + text + "\""));
		}
	}

	private void CopyFullPath()
	{
		MediaFile? selectedMediaFile = SelectedMediaFile;
		string text = ((selectedMediaFile != null) ? selectedMediaFile.FilePath : null);
		if (text != null)
		{
			Clipboard.SetText(text);
		}
	}

	private void CopyToFolder()
	{
		if (HasMultiSelection)
		{
			var files = MultiSelectedItems
				.Where(f => f.FilePath != null && File.Exists(f.FilePath))
				.ToList();
			if (files.Count == 0) return;

			var dlg = new OpenFolderDialog { Title = "Copy photos to folder…" };
			if (dlg.ShowDialog() != true) return;

			string destFolder = dlg.FolderName;
			int copied = 0, skipped = 0;
			foreach (var file in files)
			{
				string dest = Path.Combine(destFolder, Path.GetFileName(file.FilePath));
				if (string.Equals(file.FilePath, dest, StringComparison.OrdinalIgnoreCase)) continue;
				if (File.Exists(dest)) { skipped++; continue; }
				File.Copy(file.FilePath, dest);
				copied++;
			}
			StatusText = skipped > 0
				? $"Copied {copied} photo{(copied == 1 ? "" : "s")} ({skipped} skipped — already exist)"
				: $"Copied {copied} photo{(copied == 1 ? "" : "s")} to {Path.GetFileName(destFolder)}";
		}
		else
		{
			MediaFile? selectedMediaFile = SelectedMediaFile;
			string text = ((selectedMediaFile != null) ? selectedMediaFile.FilePath : null);
			if (text == null || !File.Exists(text))
			{
				return;
			}
			SaveFileDialog saveFileDialog = new SaveFileDialog
			{
				Title = "Copy photo to…",
				FileName = Path.GetFileName(text),
				Filter = "All Files|*.*",
				InitialDirectory = Path.GetDirectoryName(text)
			};
			if (saveFileDialog.ShowDialog() == true)
			{
				string fileName = saveFileDialog.FileName;
				if (!string.Equals(text, fileName, StringComparison.OrdinalIgnoreCase))
				{
					File.Copy(text, fileName, overwrite: false);
					StatusText = "Copied to " + Path.GetFileName(fileName);
				}
			}
		}
	}

	public IAsyncRelayCommand EditDateTakenCommand =>
		_editDateTakenCommand ??= new AsyncRelayCommand(EditDateTakenAsync, () => SelectedMediaFile != null && !SelectedIsOffline);
	private AsyncRelayCommand? _editDateTakenCommand;

	private async Task EditDateTakenAsync()
	{
		var mf = SelectedMediaFile;
		if (mf == null) return;

		var dlg = new PhotoWell.Desktop.Views.EditDateDialog(mf.DateTaken)
		{
			Owner = Application.Current.MainWindow
		};
		if (dlg.ShowDialog() != true || dlg.ResultDateTime is not { } newDate) return;

		// Persist to database
		mf.DateTaken = newDate;
		await WithRepo(r => ((IRepository<MediaFile>)(object)r).UpdateAsync(mf));
		OnPropertyChanged(nameof(SelectedDateText));
		OnPropertyChanged(nameof(SelectedTimeText));

		if (dlg.WriteToFile && File.Exists(mf.FilePath))
		{
			try
			{
				if (dlg.CreateBackup)
					File.Copy(mf.FilePath, mf.FilePath + ".bak", overwrite: true);

				var progress = new Progress<string>(msg => StatusText = msg);
				await _exifEditService.EnsureAvailableAsync(progress);

				var formatted = newDate.ToString("yyyy:MM:dd HH:mm:ss");
				await _exifEditService.WriteTagAsync(mf.FilePath, "DateTimeOriginal", formatted);
				await _exifEditService.WriteTagAsync(mf.FilePath, "CreateDate", formatted);

				StatusText = dlg.CreateBackup
					? "Date updated and written to file (original backed up as .bak)"
					: "Date updated and written to file";
			}
			catch (Exception ex)
			{
				AppLog.Error($"EditDateTaken file write failed: {ex.Message}");
				StatusText = $"Date saved to library, but file write failed: {ex.Message}";
			}
		}
		else
		{
			StatusText = "Date updated in library";
		}
	}

	private static void ShowWarning(string message, string title = "PhotoWell")
	{
		MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Exclamation);
	}

	private void RebuildSendToMenuItems()
	{
		SendToMenuItems.Clear();
		SendToMenuItems.Add(new SendToMenuItem("Email…", (ICommand?)SendToEmailCommand));
		SendToMenuItems.Add(new SendToMenuItem("Print", (ICommand?)SendToPrintCommand));
		SendToMenuItems.Add(new SendToMenuItem("Set as Desktop Wallpaper", (ICommand?)SetAsWallpaperCommand));
		SendToMenuItems.Add(new SendToMenuItem("Open With…", (ICommand?)SendToOpenWithCommand));
		List<ExternalEditorEntry> externalEditors = UserPreferences.Current.ExternalEditors;
		if (externalEditors.Count <= 0)
		{
			return;
		}
		SendToMenuItems.Add(new SendToMenuItem(isSeparator: true));
		foreach (ExternalEditorEntry item in externalEditors)
		{
			SendToMenuItems.Add(new SendToMenuItem(item.Name, (ICommand?)OpenInExternalEditorCommand, item.ExePath));
		}
	}

	private void SendToEmail()
	{
		MediaFile? selectedMediaFile = SelectedMediaFile;
		string text = ((selectedMediaFile != null) ? selectedMediaFile.FilePath : null);
		if (text == null || !File.Exists(text))
		{
			return;
		}
		CloseOpenContextMenus();
		string filePath = text;
		string subject = Path.GetFileNameWithoutExtension(text);
		Task.Run(delegate
		{
			try
			{
				NativeMethods.MapiSendFile(filePath, subject);
			}
			catch (Exception ex)
			{
				AppLog.Error("SendToEmail (MAPI) failed: " + ex.GetType().Name + ": " + ex.Message);
				((DispatcherObject)Application.Current).Dispatcher.Invoke((Action)delegate
				{
					ShowWarning("Could not open your email client.\n\nMake sure a mail app (Outlook, Thunderbird, Windows Mail, etc.) is installed and set as your default.", "Send to Email");
				});
			}
		});
	}

	private static void CloseOpenContextMenus()
	{
		foreach (Window window in Application.Current.Windows)
		{
			foreach (ContextMenu item in FindVisualChildren<ContextMenu>((DependencyObject)(object)window))
			{
				if (item.IsOpen)
				{
					item.IsOpen = false;
				}
			}
		}
	}

	private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
	{
		int count = VisualTreeHelper.GetChildrenCount(parent);
		for (int i = 0; i < count; i++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(parent, i);
			T val = (T)(object)((child is T) ? child : null);
			if (val != null)
			{
				yield return val;
			}
			foreach (T item in FindVisualChildren<T>(child))
			{
				yield return item;
			}
		}
	}

	private void SendToPrint()
	{
		MediaFile? selectedMediaFile = SelectedMediaFile;
		string text = ((selectedMediaFile != null) ? selectedMediaFile.FilePath : null);
		if (text == null || !File.Exists(text))
		{
			return;
		}
		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = text,
				Verb = "print",
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			AppLog.Error($"SendToPrint failed for '{text}': {ex.GetType().Name}: {ex.Message}");
			ShowWarning("Windows could not find a print handler for this file type.\n\nTry opening the photo in another app first, then print from there.", "Print");
		}
	}

	private void SetAsWallpaper()
	{
		MediaFile? selectedMediaFile = SelectedMediaFile;
		string text = ((selectedMediaFile != null) ? selectedMediaFile.FilePath : null);
		if (text == null || !File.Exists(text))
		{
			return;
		}
		string lpvParam = text;
		string item = Path.GetExtension(text).TrimStart('.').ToLowerInvariant();
		if (!SupportedFormats.JpegExtensions.Contains(item))
		{
			string text2 = SelectedMediaFile.ThumbnailLarge ?? SelectedMediaFile.ThumbnailMedium;
			if (text2 == null || !File.Exists(text2))
			{
				ShowWarning("Windows only supports JPEG, PNG, or BMP files as desktop wallpaper.\n\nRe-analyze this photo to generate a JPEG thumbnail that can be used instead.", "Set as Desktop Wallpaper");
				return;
			}
			lpvParam = text2;
		}
		NativeMethods.SystemParametersInfo(20, 0, lpvParam, 3);
	}

	private void SendToOpenWith()
	{
		MediaFile? selectedMediaFile = SelectedMediaFile;
		string text = ((selectedMediaFile != null) ? selectedMediaFile.FilePath : null);
		if (text == null || !File.Exists(text))
		{
			return;
		}
		try
		{
			NativeMethods.ShowOpenWithDialog(new WindowInteropHelper(Application.Current.MainWindow).Handle, text);
		}
		catch (Exception ex)
		{
			AppLog.Error("SendToOpenWith failed: " + ex.GetType().Name + ": " + ex.Message);
			ShowWarning("Could not open the 'Open With' dialog.\n\nTry right-clicking the file in Windows Explorer instead.", "Open With");
		}
	}

	private void OpenInExternalEditor(string exePath)
	{
		MediaFile? selectedMediaFile = SelectedMediaFile;
		string text = ((selectedMediaFile != null) ? selectedMediaFile.FilePath : null);
		if (text == null || !File.Exists(text))
		{
			return;
		}
		try
		{
			Process.Start(new ProcessStartInfo(exePath)
			{
				Arguments = "\"" + text + "\"",
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			MessageBox.Show("Failed to open editor:\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private bool CanFileOp()
	{
		if (SelectedMediaFile != null)
		{
			return !SelectedIsOffline;
		}
		return false;
	}

	private async Task ExcludeFolderAsync()
	{
		if (SelectedMediaFile == null)
		{
			return;
		}
		string folderPath = Path.GetDirectoryName(SelectedMediaFile.FilePath);
		if (string.IsNullOrEmpty(folderPath))
		{
			return;
		}
		ExcludeFolderDialog dialog = new ExcludeFolderDialog(folderPath, GetActiveWindow());
		if (dialog.ShowDialog() != true)
		{
			return;
		}
		string normalizedFolder = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		List<MediaFile> toRemove = MediaFiles.Where(delegate(MediaFile mf)
		{
			string text = (Path.GetDirectoryName(mf.FilePath) ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			if (!dialog.IncludeSubfolders)
			{
				return text.Equals(normalizedFolder, StringComparison.OrdinalIgnoreCase);
			}
			return text.Equals(normalizedFolder, StringComparison.OrdinalIgnoreCase) || text.StartsWith(normalizedFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
		}).ToList();
		try
		{
			await Task.Run(async delegate
			{
				using IServiceScope scope = _scopeFactory.CreateScope();
				IExclusionRepository requiredService = scope.ServiceProvider.GetRequiredService<IExclusionRepository>();
				IMediaFileRepository mediaRepo = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
				ExclusionRule val2 = new ExclusionRule
				{
					Value = folderPath,
					IsFullPath = true
				};
				await requiredService.AddAsync(val2);
				await mediaRepo.RemoveByFolderAsync(folderPath, dialog.IncludeSubfolders);
			});
		}
		catch (Exception ex)
		{
			StatusText = "Could not exclude folder: " + ex.Message;
			return;
		}
		_excludedFullPaths.Add(folderPath);
		foreach (MediaFile item in toRemove)
		{
			_visionSkipIds.TryAdd(item.Id, 0);
		}
		if (SelectedMediaFile != null && toRemove.Any((MediaFile m) => m.Id == SelectedMediaFile.Id))
		{
			SelectedMediaFile = null;
		}
		foreach (MediaFile item2 in toRemove)
		{
			MediaFiles.Remove(item2);
		}
		PendingDescriptionCount = MediaFiles.Count((MediaFile m) => (int)m.AnalysisStatus == 4);
		StatusText = $"Folder excluded — {toRemove.Count} photo(s) removed from library.";
		MediaFile val = SelectedMediaFile ?? MediaFiles.FirstOrDefault();
		if (val != null)
		{
			this.ScrollIntoViewRequested?.Invoke(val);
		}
	}

	private async Task IncludeFolderAsync()
	{
		if (SelectedMediaFile == null)
		{
			return;
		}
		string folderPath = Path.GetDirectoryName(SelectedMediaFile.FilePath);
		if (string.IsNullOrEmpty(folderPath))
		{
			return;
		}
		using IServiceScope scope = _scopeFactory.CreateScope();
		await scope.ServiceProvider.GetRequiredService<IExclusionRepository>().RemoveByValueAsync(folderPath, true);
		_excludedFullPaths.Remove(folderPath);
		OnPropertyChanged("SelectedFolderIsExcluded");
		StatusText = "Folder included — it will appear in future scans. Re-import to restore removed photos.";
	}

	private async Task ExcludeImageAsync()
	{
		List<MediaFile> targets = (HasMultiSelection ? MultiSelectedItems.ToList() : ((SelectedMediaFile != null) ? new List<MediaFile>(1) { SelectedMediaFile } : new List<MediaFile>()));
		if (targets.Count == 0)
		{
			return;
		}
		string messageBoxText = ((targets.Count == 1) ? ("Exclude \"" + targets[0].FileName + "\" from the library?\n\nThe file will not be deleted from disk. You can re-import it later.") : $"Exclude {targets.Count} photos from the library?\n\nThe files will not be deleted from disk. You can re-import them later.");
		if (MessageBox.Show(GetActiveWindow(), messageBoxText, "Exclude from Library", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
		{
			return;
		}
		foreach (MediaFile mf in targets)
		{
			_visionSkipIds.TryAdd(mf.Id, 0);
			await WithRepo((IMediaFileRepository r) => r.ExcludeAsync(mf.Id));
			MediaFiles.Remove(mf);
		}
		MultiSelectedItems.Clear();
		NotifyMultiSelectChanged();
		SelectedMediaFile = null;
		PhotoCount = MediaFiles.Count;
		StatusText = $"Excluded {targets.Count} photo(s) — will not be reimported during future scans";
	}

	private async Task RemoveFromLibraryAsync()
	{
		List<MediaFile> targets = (HasMultiSelection ? MultiSelectedItems.ToList() : ((SelectedMediaFile != null) ? new List<MediaFile>(1) { SelectedMediaFile } : new List<MediaFile>()));
		if (targets.Count == 0)
		{
			return;
		}
		string messageBoxText = ((targets.Count == 1) ? ("Remove \"" + targets[0].FileName + "\" from the library?\n\nThe file stays on disk, but all tags, descriptions, and album memberships will be lost.") : $"Remove {targets.Count} photos from the library?\n\nThe files stay on disk, but all tags, descriptions, and album memberships will be lost.");
		if (MessageBox.Show(GetActiveWindow(), messageBoxText, "Remove from Library", MessageBoxButton.YesNo, MessageBoxImage.Exclamation, MessageBoxResult.No) != MessageBoxResult.Yes)
		{
			return;
		}
		foreach (MediaFile mf in targets)
		{
			_visionSkipIds.TryAdd(mf.Id, 0);
			await WithRepo((IMediaFileRepository r) => ((IRepository<MediaFile>)(object)r).DeleteAsync(mf.Id));
			MediaFiles.Remove(mf);
		}
		MultiSelectedItems.Clear();
		NotifyMultiSelectChanged();
		SelectedMediaFile = null;
		PhotoCount = MediaFiles.Count;
		StatusText = $"Removed {targets.Count} photo(s) from library";
		LoadSidebarLibrariesAsync();
	}

	private async Task DeletePermanentlyAsync()
	{
		List<MediaFile> list = (HasMultiSelection ? MultiSelectedItems.ToList() : ((SelectedMediaFile != null) ? new List<MediaFile>(1) { SelectedMediaFile } : new List<MediaFile>()));
		if (list.Count == 0)
		{
			return;
		}
		string messageBoxText = ((list.Count == 1) ? ("Permanently delete \"" + list[0].FileName + "\" from disk?\n\nThis cannot be undone.") : $"Permanently delete {list.Count} photos from disk?\n\nThis cannot be undone.");
		if (MessageBox.Show(GetActiveWindow(), messageBoxText, "Delete Permanently", MessageBoxButton.YesNo, MessageBoxImage.Exclamation, MessageBoxResult.No) != MessageBoxResult.Yes)
		{
			return;
		}
		int deleted = 0;
		int diskFailed = 0;
		foreach (MediaFile mf in list)
		{
			_visionSkipIds.TryAdd(mf.Id, 0);
			try
			{
				if (File.Exists(mf.FilePath))
				{
					File.Delete(mf.FilePath);
				}
			}
			catch (Exception ex)
			{
				diskFailed++;
				AppLog.Error("Failed to delete \"" + mf.FilePath + "\": " + ex.Message);
			}
			await WithRepo((IMediaFileRepository r) => ((IRepository<MediaFile>)(object)r).DeleteAsync(mf.Id));
			MediaFiles.Remove(mf);
			deleted++;
		}
		MultiSelectedItems.Clear();
		NotifyMultiSelectChanged();
		SelectedMediaFile = null;
		PhotoCount = MediaFiles.Count;
		StatusText = ((diskFailed > 0) ? $"Removed {deleted} photo{((deleted == 1) ? "" : "s")} from library — {diskFailed} file{((diskFailed == 1) ? "" : "s")} could not be deleted from disk (may be open in another app)" : $"Deleted {deleted} photo{((deleted == 1) ? "" : "s")} permanently");
		LoadSidebarLibrariesAsync();
	}

	private async Task RegenerateThumbnailsAsync()
	{
		if (Progress.IsRunning)
		{
			if (MessageBox.Show("\"" + Progress.Heading + "\" is currently running.\n\nStop it and regenerate all thumbnails instead?", "Regenerate Thumbnails", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
			{
				return;
			}
			Progress.ForceCancel();
			while (Progress.IsRunning)
			{
				await Task.Delay(100);
			}
		}
		bool resume = false;
		if (ImportService.HasThumbnailRegenCheckpoint())
		{
			resume = MessageBox.Show("A previous thumbnail regeneration was interrupted.\n\nResume where it left off?", "Resume Thumbnail Regeneration", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;
		}
		Action<MediaFile> onThumbnailRegenerated = delegate(MediaFile updated)
		{
			((DispatcherObject)Application.Current).Dispatcher.Invoke((Action)delegate
			{
				MediaFile val = MediaFiles.FirstOrDefault((MediaFile m) => m.Id == updated.Id);
				if (val != null)
				{
					val.ThumbnailSmall = updated.ThumbnailSmall;
					val.ThumbnailMedium = updated.ThumbnailMedium;
					val.ThumbnailLarge = updated.ThumbnailLarge;
					val.Width = updated.Width;
					val.Height = updated.Height;
				}
			});
		};
		AttachTaskbar(Progress);
		await Progress.RunRegenerateThumbnailsAsync(onThumbnailRegenerated, resume);
		ClearTaskbar();
		StatusText = Progress.ResultMessage;
		await LoadAsync();
	}

	private void OpenPhotoViewer()
	{
		//IL_00f3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fd: Expected O, but got Unknown
		//IL_0142: Unknown result type (might be due to invalid IL or missing references)
		//IL_014c: Expected O, but got Unknown
		//IL_015e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0168: Expected O, but got Unknown
		//IL_017a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0184: Expected O, but got Unknown
		//IL_0196: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a0: Expected O, but got Unknown
		//IL_01b2: Unknown result type (might be due to invalid IL or missing references)
		//IL_01bc: Expected O, but got Unknown
		if (SelectedMediaFile == null)
		{
			return;
		}
		int num = MediaFiles.IndexOf(SelectedMediaFile);
		if (num < 0)
		{
			return;
		}
		using IServiceScope serviceScope = _scopeFactory.CreateScope();
		PhotoViewerWindow window = serviceScope.ServiceProvider.GetRequiredService<PhotoViewerWindow>();
		window.Owner = Application.Current.MainWindow;
		PhotoViewerViewModel vm = (PhotoViewerViewModel)window.DataContext;
		vm.PhotoChanged = delegate(MediaFile mf)
		{
			SelectedMediaFile = mf;
			vm.IsCurrentFolderExcluded = SelectedFolderIsExcluded;
			this.ScrollIntoViewRequested?.Invoke(mf);
		};
		vm.PhotoReanalyzed = updated => vm.ApplyReanalyzedPhoto(updated);
		_activeViewerVm = vm;
		vm.OpenInExplorerCommand = (ICommand?)OpenInExplorerCommand;
		vm.CopyFullPathCommand = (ICommand?)CopyFullPathCommand;
		vm.CopyToFolderCommand = (ICommand?)CopyToFolderCommand;
		vm.ReanalyzeSelectedCommand = (ICommand?)ReanalyzeSelectedCommand;
		vm.ToggleFavoriteCommand = (ICommand?)ToggleFavoriteCommand;
		vm.ExportAsBundleCommand = (ICommand?)new AsyncRelayCommand((Func<Task>)async delegate
		{
			if (vm.Current != null)
			{
				await OpenBackupWindowAsync(new Guid[1] { vm.Current.Id });
			}
		});
		vm.RemoveFromLibraryLabel = RemoveFromLibraryLabel;
		vm.DeletePermanentlyLabel = DeletePermanentlyLabel;
		vm.SendToMenuItems = SendToMenuItems;
		vm.ExcludeFolderCommand = (ICommand?)new AsyncRelayCommand((Func<Task>)async delegate
		{
			await ExcludeFolderAsync();
			vm.UpdatePhotoList(MediaFiles.ToList());
			vm.IsCurrentFolderExcluded = SelectedFolderIsExcluded;
		});
		vm.IncludeFolderCommand = (ICommand?)new AsyncRelayCommand((Func<Task>)async delegate
		{
			await IncludeFolderAsync();
			vm.IsCurrentFolderExcluded = SelectedFolderIsExcluded;
		});
		vm.ExcludeImageCommand = (ICommand?)new AsyncRelayCommand((Func<Task>)async delegate
		{
			await ExcludeImageAsync();
			vm.UpdatePhotoList(MediaFiles.ToList());
		});
		vm.RemoveFromLibraryCommand = (ICommand?)new AsyncRelayCommand((Func<Task>)async delegate
		{
			await RemoveFromLibraryAsync();
			vm.UpdatePhotoList(MediaFiles.ToList());
		});
		vm.DeletePermanentlyCommand = (ICommand?)new AsyncRelayCommand((Func<Task>)async delegate
		{
			await DeletePermanentlyAsync();
			vm.UpdatePhotoList(MediaFiles.ToList());
		});
		vm.RotateClockwiseCommand = new AsyncRelayCommand(async () =>
		{
			await RotateSelectedAsync(90);
			RefreshItemInPlace(SelectedMediaFile!);
			await vm.ReloadCurrentImageAsync();
		});
		vm.RotateCounterClockwiseCommand = new AsyncRelayCommand(async () =>
		{
			await RotateSelectedAsync(-90);
			RefreshItemInPlace(SelectedMediaFile!);
			await vm.ReloadCurrentImageAsync();
		});
		vm.RequestShowPerson += delegate(Guid personId)
		{
			window.Close();
			FilterByPersonAsync(personId);
		};
		vm.Open(MediaFiles.ToList(), num);
		window.ShowDialog();
		_activeViewerVm = null;
	}

	private async Task OpenSettingsAsync()
	{
		using IServiceScope scope = _scopeFactory.CreateScope();
		SettingsWindow requiredService = scope.ServiceProvider.GetRequiredService<SettingsWindow>();
		requiredService.Owner = Application.Current.MainWindow;
		requiredService.ShowDialog();
		SettingsViewModel vm = requiredService.DataContext as SettingsViewModel;
		if (vm?.LibraryModified ?? false)
		{
			await LoadExclusionCacheAsync();
			await LoadAsync();
			StatusText = "Excluded folder removed from library.";
		}
		if (vm?.RebuildSearchIndexRequested ?? false)
		{
			RebuildSearchIndexBackgroundAsync();
		}
		RebuildSendToMenuItems();
	}

	private async Task OpenBackupWindowAsync(IReadOnlyList<Guid>? selectedIds = null)
	{
		using IServiceScope serviceScope = _scopeFactory.CreateScope();
		BackupViewModel backupViewModel = new BackupViewModel(serviceScope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(), selectedIds);
		backupViewModel.RestoreCompleted += delegate
		{
			((DispatcherObject)Application.Current).Dispatcher.InvokeAsync<Task>((Func<Task>)async delegate
			{
				await LoadAsync();
			});
		};
		backupViewModel.BundleImportCompleted += delegate
		{
			((DispatcherObject)Application.Current).Dispatcher.InvokeAsync<Task>((Func<Task>)async delegate
			{
				await LoadAsync();
			});
		};
		BackupWindow backupWindow = new BackupWindow(backupViewModel);
		backupWindow.Owner = Application.Current.MainWindow;
		backupWindow.ShowDialog();
	}

	private async Task OpenBackupWindowForAlbumAsync(LibraryNodeViewModel? node)
	{
		if (node != null && !node.IsLibrary)
		{
			List<Guid> selectedIds = (await WithLibRepo((ILibraryRepository r) => r.GetPhotosByAlbumAsync(node.Id))).Select((MediaFile p) => p.Id).ToList();
			await OpenBackupWindowAsync(selectedIds);
		}
	}

	private async Task ScanDrivesAsync()
	{
		if (await CheckExpressCeilingAsync())
		{
			return;
		}
		using IServiceScope scope = _scopeFactory.CreateScope();
		ScanDrivesWindow requiredService = scope.ServiceProvider.GetRequiredService<ScanDrivesWindow>();
		requiredService.Owner = Application.Current.MainWindow;
		requiredService.ShowDialog();
		IReadOnlyList<string> selectedFolderPaths = ((ScanDrivesViewModel)requiredService.DataContext).SelectedFolderPaths;
		if (selectedFolderPaths.Count > 0)
		{
			if (!Progress.IsRunning)
			{
				AttachTaskbar(Progress);
				AttachPhotoCallback(Progress);
				AttachExpressLimitCallback(Progress);
			}
			await Progress.RunMultiFolderAsync(selectedFolderPaths);
			if (!Progress.IsRunning)
			{
				ClearTaskbar();
				StatusText = Progress.ResultMessage;
			}
			_folderWatcher.RefreshFoldersAsync(default(CancellationToken));
		}
		await LoadAsync();
	}

	private async Task FindDuplicatesAsync()
	{
		using IServiceScope scope = _scopeFactory.CreateScope();
		FindDuplicatesWindow requiredService = scope.ServiceProvider.GetRequiredService<FindDuplicatesWindow>();
		requiredService.Owner = Application.Current.MainWindow;
		requiredService.ShowDialog();
		await LoadAsync();
	}

	private async Task RotateClockwiseAsync()
	{
		await RotateSelectedAsync(90);
	}

	private async Task RotateCounterClockwiseAsync()
	{
		await RotateSelectedAsync(-90);
	}

	private async Task RotateSelectedAsync(int degrees)
	{
		List<MediaFile> targets = (HasMultiSelection ? MultiSelectedItems.ToList() : ((SelectedMediaFile != null) ? new List<MediaFile>(1) { SelectedMediaFile } : new List<MediaFile>()));
		if (targets.Count == 0)
		{
			return;
		}
		StatusText = ((targets.Count == 1) ? ("Rotating \"" + targets[0].FileName + "\"…") : $"Rotating {targets.Count} photos…");
		using IServiceScope scope = _scopeFactory.CreateScope();
		IThumbnailService thumbs = scope.ServiceProvider.GetRequiredService<IThumbnailService>();
		foreach (MediaFile mf in targets)
		{
			mf.UserRotation = ((mf.UserRotation + degrees) % 360 + 360) % 360;
			await WithRepo((IMediaFileRepository r) => ((IRepository<MediaFile>)(object)r).UpdateAsync(mf));
			ThumbnailResult val = await thumbs.GenerateThumbnailsAsync(mf, default(CancellationToken));
			if (val.Success)
			{
				mf.ThumbnailSmall = val.SmallPath;
				mf.ThumbnailMedium = val.MediumPath;
				mf.ThumbnailLarge = val.LargePath;
				await WithRepo((IMediaFileRepository r) => ((IRepository<MediaFile>)(object)r).UpdateAsync(mf));
			}
			mf.LoadedThumbnail = null;
			LoadSingleThumbnailAsync(mf);
		}
		StatusText = ((targets.Count == 1) ? ("Rotated \"" + targets[0].FileName + "\"") : $"Rotated {targets.Count} photos");
	}

	private async Task ToggleFavoriteAsync()
	{
		List<MediaFile> list = (HasMultiSelection ? MultiSelectedItems.ToList() : ((SelectedMediaFile != null) ? new List<MediaFile>(1) { SelectedMediaFile } : new List<MediaFile>()));
		if (list.Count == 0)
		{
			return;
		}
		bool newState = !list[0].IsFavorite;
		foreach (MediaFile t in list)
		{
			t.IsFavorite = newState;
			await WithRepo((IMediaFileRepository r) => ((IRepository<MediaFile>)(object)r).UpdateAsync(t));
			RefreshItemInPlace(t);
		}
		OnPropertyChanged("SelectedIsFavorite");
		if (ActiveView == GalleryView.Favorites && !newState)
		{
			await LoadAsync();
		}
	}

	private void InvalidateSelectionDependentProperties()
	{
		OnPropertyChanged("HasSelection");
		OnPropertyChanged("HasAnySelected");
		OnPropertyChanged("SelectedIsOffline");
		OnPropertyChanged("IsReanalyzeSelectedEnabled");
		OnPropertyChanged("SelectedFolderIsExcluded");
		OnPropertyChanged("HasAiDescription");
		OnPropertyChanged("DescriptionAttemptedButFailed");
		OnPropertyChanged("SelectedHasEmbedding");
		((IRelayCommand)FindSimilarCommand).NotifyCanExecuteChanged();
	}

	private void InvalidateDescriptionAndCaption()
	{
		IsEditingDescription = false;
		RefreshDescriptionProperties();
		IsEditingCaption = false;
		RefreshCaptionProperties();
	}

	private void InvalidateExifProperties()
	{
		OnPropertyChanged("SelectedDateText");
		OnPropertyChanged("SelectedTimeText");
		OnPropertyChanged("SelectedDimensionsText");
		OnPropertyChanged("SelectedFileSizeText");
		OnPropertyChanged("SelectedIsFavorite");
		OnPropertyChanged("SelectedApertureText");
		OnPropertyChanged("SelectedShutterText");
		OnPropertyChanged("SelectedIsoText");
		OnPropertyChanged("SelectedFocalLengthText");
		OnPropertyChanged("HasAperture");
		OnPropertyChanged("HasShutterSpeed");
		OnPropertyChanged("HasIso");
		OnPropertyChanged("HasFocalLength");
		OnPropertyChanged("HasExifData");
		OnPropertyChanged("HasGps");
		OnPropertyChanged("SelectedGpsText");
	}

	private void LoadSelectionAsync(MediaFile? value)
	{
		//IL_0011: Unknown result type (might be due to invalid IL or missing references)
		//IL_0017: Invalid comparison between Unknown and I4
		if (value != null)
		{
			LoadTagsAsync(value.Id);
			if ((int)value.AnalysisStatus == 4)
			{
				PrioritizeVision(value.Id);
			}
		}
	}

	private async Task LoadTagsAsync(Guid id)
	{
		MediaFile val = await WithRepo((IMediaFileRepository r) => ((IRepository<MediaFile>)(object)r).GetByIdAsync(id));
		SelectedTags.Clear();
		if (((val != null) ? val.Tags : null) != null)
		{
			foreach (Tag item in from t in val.Tags
				orderby t.IsAIGenerated descending, t.Confidence descending
				select t)
			{
				SelectedTags.Add(new TagViewModel(item.Id, item.Name, item.IsAIGenerated));
			}
		}
		OnPropertyChanged("HasSelectedTags");
	}

	private async Task RemoveTagAsync(TagViewModel tag)
	{
		if (SelectedMediaFile != null)
		{
			await WithRepo((IMediaFileRepository r) => r.RemoveTagFromPhotoAsync(SelectedMediaFile.Id, tag.Id));
			SelectedTags.Remove(tag);
			OnPropertyChanged("HasSelectedTags");
		}
	}

	private async Task LoadAllMetadataAsync()
	{
		if (SelectedMediaFile == null || _allMetadataLoaded)
		{
			return;
		}
		string filePath = SelectedMediaFile.FilePath;
		IReadOnlyList<MetadataTag> readOnlyList;
		using (IServiceScope scope = _scopeFactory.CreateScope())
		{
			readOnlyList = await scope.ServiceProvider.GetRequiredService<IImportService>().ReadAllTagsAsync(filePath, default(CancellationToken));
		}
		AllMetadata.Clear();
		foreach (MetadataTag item in readOnlyList)
		{
			AllMetadata.Add(item);
		}
		_allMetadataLoaded = true;
	}

	private async Task ShowAddTagPopup()
	{
		if (!HasAnySelected)
		{
			return;
		}
		using IServiceScope scope = _scopeFactory.CreateScope();
		_allTagNames = await scope.ServiceProvider.GetRequiredService<IMediaFileRepository>().GetAllTagNamesAsync();
		BatchTagText = "";
		TagSuggestions.Clear();
		OnPropertyChanged("AddTagPopupTitle");
		IsAddTagPopupOpen = true;
	}

	private async Task ConfirmBatchTag()
	{
		string name = BatchTagText.Trim();
		IsAddTagPopupOpen = false;
		if (string.IsNullOrEmpty(name))
		{
			return;
		}
		List<MediaFile> list = (HasMultiSelection ? MultiSelectedItems.ToList() : ((SelectedMediaFile != null) ? new List<MediaFile>(1) { SelectedMediaFile } : new List<MediaFile>()));
		if (list.Count == 0)
		{
			return;
		}
		int added = 0;
		Guid? newTagId = null;
		bool selectedPhotoTagged = false;
		foreach (MediaFile photo in list)
		{
			using IServiceScope scope = _scopeFactory.CreateScope();
			IMediaFileRepository repo = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
			MediaFile mf = await ((IRepository<MediaFile>)(object)repo).GetByIdAsync(photo.Id);
			if (mf != null && !mf.Tags.Any((Tag t) => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
			{
				Tag val = await repo.GetOrCreateTagAsync(name, name.ToLowerInvariant(), (TagCategory)11, false, 0f);
				newTagId = val.Id;
				mf.Tags.Add(val);
				await ((IRepository<MediaFile>)(object)repo).UpdateAsync(mf);
				added++;
				MediaFile? selectedMediaFile = SelectedMediaFile;
				if (((selectedMediaFile != null) ? new Guid?(selectedMediaFile.Id) : ((Guid?)null)) == photo.Id)
				{
					selectedPhotoTagged = true;
				}
			}
		}
		if (selectedPhotoTagged && newTagId.HasValue)
		{
			SelectedTags.Add(new TagViewModel(newTagId.Value, name, IsAIGenerated: false));
			OnPropertyChanged("HasSelectedTags");
		}
		if (added > 0 && !_allTagNames.Contains<string>(name, StringComparer.OrdinalIgnoreCase))
		{
			_allTagNames.Add(name);
		}
		StatusText = ((added > 0) ? $"Tag \"{name}\" added to {added} photo{((added == 1) ? "" : "s")}" : ("Tag \"" + name + "\" already present on all selected photos"));
	}

	private void CloseAddTagPopup()
	{
		IsAddTagPopupOpen = false;
	}

	private void SelectTagSuggestion(string tag)
	{
		BatchTagText = tag;
		TagSuggestions.Clear();
	}

	private async Task AddTagAsync()
	{
		if (SelectedMediaFile == null || string.IsNullOrWhiteSpace(NewTagText))
		{
			return;
		}
		string name = NewTagText.Trim();
		if (SelectedTags.Any((TagViewModel t) => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
		{
			NewTagText = "";
			return;
		}
		using IServiceScope scope = _scopeFactory.CreateScope();
		IMediaFileRepository repo = scope.ServiceProvider.GetRequiredService<IMediaFileRepository>();
		MediaFile mf = await ((IRepository<MediaFile>)(object)repo).GetByIdAsync(SelectedMediaFile.Id);
		if (mf != null)
		{
			Tag tag = await repo.GetOrCreateTagAsync(name, name.ToLowerInvariant(), (TagCategory)11, false, 0f);
			if (!mf.Tags.Any((Tag t) => t.Id == tag.Id))
			{
				mf.Tags.Add(tag);
				await ((IRepository<MediaFile>)(object)repo).UpdateAsync(mf);
			}
			SelectedTags.Add(new TagViewModel(tag.Id, name, IsAIGenerated: false));
			OnPropertyChanged("HasSelectedTags");
			NewTagText = "";
		}
	}

	public async Task RefreshOfflineStatusAsync()
	{
		List<MediaFile> snapshot = MediaFiles.ToList();
		OfflineCount = await Task.Run(() => MarkOfflineFiles(snapshot));
		OnPropertyChanged("HasOfflineFiles");
		OnPropertyChanged("OfflineStatusText");
		OnPropertyChanged("SelectedIsOffline");
	}

	private void NotifyPropertiesChanged(params string[] propertyNames)
	{
		foreach (string text in propertyNames)
		{
			OnPropertyChanged(text);
		}
	}

	private static int MarkOfflineFiles(IEnumerable<MediaFile> files)
	{
		Dictionary<string, bool> dictionary = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
		int num = 0;
		foreach (MediaFile file in files)
		{
			string pathRoot = Path.GetPathRoot(file.FilePath);
			if (!string.IsNullOrEmpty(pathRoot))
			{
				if (!dictionary.TryGetValue(pathRoot, out var value))
				{
					value = (dictionary[pathRoot] = Directory.Exists(pathRoot));
				}
				file.IsOffline = !value;
				if (file.IsOffline)
				{
					num++;
				}
			}
		}
		return num;
	}

	private bool MatchesCurrentView(MediaFile mf)
	{
		if (ActiveView == GalleryView.Favorites)
		{
			return false;
		}
		if (ActiveView == GalleryView.Album)
		{
			return false;
		}
		if (IsRelatedImagesMode)
		{
			return false;
		}
		if (!IsSearchActive)
		{
			return true;
		}
		if (IsSemanticSearch)
		{
			return false;
		}
		if (IsSimilaritySearch)
		{
			return false;
		}
		string q = SearchQuery.Trim();
		if (!Has(mf.FileName) && !Has(mf.AiDescription) && !Has(mf.UserDescription) && !Has(mf.UserCaption) && !Has(mf.CameraMake) && !Has(mf.CameraModel))
		{
			return mf.Tags?.Any((Tag t) => Has(t.Name)) ?? false;
		}
		return true;
		bool Has(string? s)
		{
			if (!string.IsNullOrEmpty(s))
			{
				return s.Contains(q, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnSelectedMediaFileChanged(MediaFile? value)
	{
		_selectionAnchor = value;
		InvalidateSelectionDependentProperties();
		InvalidateDescriptionAndCaption();
		InvalidateExifProperties();
		SelectedTags.Clear();
		OnPropertyChanged("HasSelectedTags");
		AllMetadata.Clear();
		_allMetadataLoaded = false;
		LoadSelectionAsync(value);
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnRelatedTimeValueChanged(int value)
	{
		UserPreferences.Current.RelatedTimeValue = Math.Max(1, value);
		UserPreferences.Current.Save();
		ScheduleRelatedRefresh();
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnRelatedTimeUnitIndexChanged(int value)
	{
		UserPreferences.Current.RelatedTimeUnitIndex = value;
		UserPreferences.Current.Save();
		ScheduleRelatedRefresh();
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnRelatedDistanceStepIndexChanged(int value)
	{
		UserPreferences.Current.RelatedDistanceStepIndex = value;
		UserPreferences.Current.Save();
		ScheduleRelatedRefresh();
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnActiveViewChanged(GalleryView value)
	{
		NotifyPropertiesChanged("IsAllPhotosActive", "IsFavoritesActive", "IsAlbumActive", "IsPeopleActive", "IsOnThisDayActive", "IsGalleryVisible");
		if (value != GalleryView.OnThisDay)
		{
			LoadAsync();
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnSimilarityCountChanged(int value)
	{
		if (IsSimilaritySearch)
		{
			ScheduleSimilarityRefresh();
		}
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnIsEditingDescriptionChanged(bool value)
	{
		RefreshDescriptionProperties();
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnIsEditingCaptionChanged(bool value)
	{
		RefreshCaptionProperties();
	}

	[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "8.4.0.0")]
	private void OnBatchTagTextChanged(string value)
	{
		TagSuggestions.Clear();
		if (string.IsNullOrWhiteSpace(value))
		{
			return;
		}
		foreach (string item in _allTagNames.Where((string n) => n.Contains(value, StringComparison.OrdinalIgnoreCase)).Take(6))
		{
			TagSuggestions.Add(item);
		}
	}

	// ── IAssistantActions implementation ─────────────────────────────────────

	public async Task<string> ChatSearchAsync(string query)
	{
		await Application.Current.Dispatcher.InvokeAsync(async () =>
		{
			PendingSearchQuery = query;
			SearchQuery = query;
			await LoadAsync();
		});
		return $"Showing {PhotoCount} photo{(PhotoCount == 1 ? "" : "s")} matching \"{query}\".";
	}

	public async Task<string> ChatFilterByPersonAsync(string name, bool includeUnconfirmed)
	{
		using var scope = _scopeFactory.CreateScope();
		var personRepo = scope.ServiceProvider.GetRequiredService<IPersonRepository>();
		var people = await personRepo.GetAllNamedAsync();
		var person = people.FirstOrDefault(p =>
			p.Name?.Equals(name, StringComparison.OrdinalIgnoreCase) == true ||
			p.NormalizedName?.Equals(name.ToLowerInvariant().Trim(), StringComparison.OrdinalIgnoreCase) == true);

		if (person == null)
		{
			// Fuzzy fallback: contains match
			person = people.FirstOrDefault(p =>
				p.Name?.Contains(name, StringComparison.OrdinalIgnoreCase) == true);
		}

		if (person == null)
			return $"No person named \"{name}\" was found in the library.";

		var confirmedIds = await WithRepo(r => r.GetFacePhotoIdsForPersonAsync(person.Id));
		var photoIdSet = new HashSet<Guid>(confirmedIds);

		if (includeUnconfirmed)
		{
			var unconfirmed = await WithRepo(r => r.GetUnconfirmedFacesForPersonAsync(person.Id));
			foreach (var (face, _) in unconfirmed)
				if (face.IdentificationConfidence >= 0.7)
					photoIdSet.Add(face.MediaFileId);
		}

		var photos = await WithRepo(r => r.GetByIdsAsync(photoIdSet));
		await Application.Current.Dispatcher.InvokeAsync(() =>
		{
			_personFilterPhotoIds = photos.Select(p => p.Id).ToList();
			MediaFiles = new System.Collections.ObjectModel.ObservableCollection<MediaFile>(photos);
			GalleryCollectionReplaced?.Invoke();
			PhotoCount = photos.Count;
			TotalLibraryCount = TotalLibraryCount; // no change
			IsPersonFilterActive = true;
			ActiveFilterLabel = $"Showing {photos.Count} photo{(photos.Count == 1 ? "" : "s")} with {person.Name}";
			StatusText = ActiveFilterLabel;
			ActiveView = GalleryView.AllPhotos;
		});

		return $"Showing {photos.Count} photo{(photos.Count == 1 ? "" : "s")} featuring {person.Name}" +
		       (includeUnconfirmed ? " (including high-confidence unconfirmed matches)." : ".");
	}

	public async Task<string> ChatFilterByDateAsync(string? startDate, string? endDate)
	{
		DateTime? from = null, to = null;
		if (!string.IsNullOrWhiteSpace(startDate) && DateTime.TryParse(startDate, out var s)) from = s;
		if (!string.IsNullOrWhiteSpace(endDate)   && DateTime.TryParse(endDate,   out var e)) to   = e.Date.AddDays(1).AddTicks(-1);

		if (from == null && to == null)
			return "No valid dates provided. Please use ISO-8601 format, e.g. 2024-01-01.";

		var photos = await WithRepo(r => r.GetByDateRangeAsync(
			from ?? DateTime.MinValue,
			to   ?? DateTime.MaxValue));

		var list = photos.ToList();
		await Application.Current.Dispatcher.InvokeAsync(() =>
		{
			MediaFiles = new System.Collections.ObjectModel.ObservableCollection<MediaFile>(list);
			GalleryCollectionReplaced?.Invoke();
			PhotoCount = list.Count;
			ActiveFilterLabel = $"Date filter: {from?.ToShortDateString() ?? "any"} – {to?.ToShortDateString() ?? "any"}";
			StatusText = $"{list.Count} photo{(list.Count == 1 ? "" : "s")} in date range";
			ActiveView = GalleryView.AllPhotos;
		});

		return $"Showing {list.Count} photo{(list.Count == 1 ? "" : "s")} taken between " +
		       $"{from?.ToShortDateString() ?? "the beginning"} and {to?.ToShortDateString() ?? "now"}.";
	}

	public async Task<string> ChatClearFiltersAsync()
	{
		await Application.Current.Dispatcher.InvokeAsync(async () =>
		{
			PendingSearchQuery = "";
			SearchQuery = "";
			_personFilterPhotoIds = Array.Empty<Guid>();
			IsPersonFilterActive = false;
			ActiveFilterLabel = "";
			_similaritySourceId = null;
			IsSimilaritySearch = false;
			await LoadAsync();
		});
		return "All filters cleared. Showing the full library.";
	}

	public async Task<string> ChatGetLibraryStatsAsync()
	{
		int total = await WithRepo(r => ((PhotoWell.Core.Interfaces.IRepository<MediaFile>)(object)r).CountAsync());
		using var scope = _scopeFactory.CreateScope();
		var personRepo = scope.ServiceProvider.GetRequiredService<IPersonRepository>();
		var people = await personRepo.GetAllNamedAsync();
		return $"Your library contains {total:N0} photo{(total == 1 ? "" : "s")} and " +
		       $"{people.Count} named {(people.Count == 1 ? "person" : "people")}.";
	}

	public Task<string> ChatOpenPhotoAsync(string filename)
	{
		var photo = MediaFiles.FirstOrDefault(m =>
			string.Equals(m.FileName, filename, StringComparison.OrdinalIgnoreCase) ||
			string.Equals(Path.GetFileName(m.FilePath), filename, StringComparison.OrdinalIgnoreCase));

		if (photo == null)
			return Task.FromResult($"No photo named \"{filename}\" is visible in the current gallery view.");

		Application.Current.Dispatcher.Invoke(() => SelectedMediaFile = photo);
		return Task.FromResult($"Opened \"{filename}\".");
	}

	public Task<string> ChatShowPeopleAsync()
	{
		Application.Current.Dispatcher.Invoke(() =>
		{
			using var scope = _scopeFactory.CreateScope();
			var win = scope.ServiceProvider.GetRequiredService<Views.PeopleWindow>();
			win.Owner = Application.Current.MainWindow;
			win.Show();
		});
		return Task.FromResult("Opened the People window.");
	}

	public string GetCurrentContext()
	{
		var sb = new System.Text.StringBuilder();
		sb.AppendLine($"Current library: {TotalLibraryCount:N0} total photos, {PhotoCount:N0} shown.");
		if (!string.IsNullOrWhiteSpace(SearchQuery))
			sb.AppendLine($"Active search: \"{SearchQuery}\".");
		if (IsPersonFilterActive && !string.IsNullOrWhiteSpace(ActiveFilterLabel))
			sb.AppendLine($"Active person filter: {ActiveFilterLabel}.");
		return sb.ToString().TrimEnd();
	}
}
