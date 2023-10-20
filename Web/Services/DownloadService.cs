﻿using System.Collections.ObjectModel;
using Polly;
using Web.Data;
using Web.Exceptions;
using Web.Models;
using Web.Models.Interfaces;

namespace Web.Services
{
    public class DownloadService : IDownloadService
    {
        private readonly HttpClient _httpClient;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IAsyncPolicy<int> _resilientStreamPolicy;
        private readonly ILogger _logger;

        private readonly Collection<DownloadElement> _pendingDownloads;
        private bool _isDownloading;

        public DownloadService(HttpClient httpClient, IServiceScopeFactory scopeFactory,
            ILogger<DownloadService> logger)
        {
            _httpClient = httpClient;
            _scopeFactory = scopeFactory;
            _logger = logger;
            _pendingDownloads = new Collection<DownloadElement>();

            _resilientStreamPolicy = Policy<int>.Handle<Exception>(AllButIoExceptions).WaitAndRetryAsync(
                5,
                retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                (exception, timeSpan, context) =>
                {
                    _logger.LogWarning(exception?.Exception,
                        "Retry download for a {0}. time after {1} seconds.", context.Count+1, timeSpan.Seconds);
                });
        }

        public IReadOnlyCollection<DownloadElement> GetPendingDownloads()
        {
            return _pendingDownloads;
        }

        public IReadOnlyCollection<DownloadElement> GetAll()
        {
            List<DownloadElement> returnList = new List<DownloadElement>(_pendingDownloads);
            using (var scope = _scopeFactory.CreateScope())
            {
                UnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<UnitOfWork>();
                returnList.AddRange(unitOfWork.DownloadRepository.GetAll());
            }

            return returnList;
        }

        private async Task<DownloadElement> CreateDownloadElement(string key, string? mediaFileKey, ElementType elementType)
        {
            using (var scope = _scopeFactory.CreateScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<UnitOfWork>();
                ISettingsService settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                IMediaElement? mediaElement = await GetMediaElement(unitOfWork, elementType, key);
                if (mediaElement == null)
                    throw new MediaNotFoundException(key);
                MediaFile? mediaFile;
                if (mediaFileKey == null)
                    mediaFile = await SelectMediaFile(mediaElement.MediaFiles, settingsService);
                else 
                    mediaFile = mediaElement.MediaFiles.FirstOrDefault(x => x.DownloadUri == mediaFileKey);
                if (mediaElement == null)
                {
                    _logger.LogError("Could not prepare download of {0} due to missing media file.", mediaElement!.Title);
                    throw new ArgumentException();
                }
                var downloadDirectory = elementType == ElementType.Movie
                    ? await settingsService.GetMovieDirectory()
                    : await settingsService.GetEpisodeDirectory();
                Directory.CreateDirectory(downloadDirectory);
                string filePath = await GetFilePath(downloadDirectory, mediaFile!.ServerFilePath, mediaElement, settingsService);
                Library? library = unitOfWork.LibraryRepository.Get(x => x.Id == mediaElement.LibraryId, null, nameof(Library.Server))
                    .FirstOrDefault();
                Uri uri = await GetCompleteDownloadUri(unitOfWork, library, mediaFile.DownloadUri);
                HttpRequestMessage httpRequestMessage = new HttpRequestMessage(HttpMethod.Get, uri);
                httpRequestMessage.Headers.Add("X-Plex-Token", library.Server.AccessToken);
                return new DownloadElement()
                {
                    Uri = uri.ToString(),
                    Name = mediaElement.Title,
                    ElementType = elementType,
                    FilePath = filePath,
                    FileName = Path.GetFileName(mediaFile.ServerFilePath),
                    TotalBytes = mediaFile.TotalBytes,
                    MediaKey = key,
                    RequestMessage = httpRequestMessage
                };
            }
        }

        private async Task<string> GetFilePath(string downloadDirectory, string serverFilePath, IMediaElement mediaElement,
            ISettingsService settingsService)
        {
            if (mediaElement is Movie movie)
            {
                var fileTemplate = await settingsService.GetMovieFileTemplate();
                switch (fileTemplate)
                {
                    case MovieFileTemplate.FilenameFromServer:
                        return Path.Combine(downloadDirectory, Path.GetFileName(serverFilePath));
                    case MovieFileTemplate.MovieDirectoryAndFilenameFromServer:
                        return Path.Combine(downloadDirectory, movie.Title,
                            Path.GetFileName(serverFilePath));
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
            else if (mediaElement is Episode episode)
            {
                var fileTemplate = await settingsService.GetEpisodeFileTemplate();
                switch (fileTemplate)
                {
                    case EpisodeFileTemplate.SeriesAndSeasonDirectoriesAndFilenameFromServer:
                        return Path.Combine(downloadDirectory, episode.TvShow.Title, $"Season {episode.SeasonNumber}",
                            Path.GetFileName(serverFilePath));
                    case EpisodeFileTemplate.SeriesDirectoryAndFilenameFromServer:
                        return Path.Combine(downloadDirectory, episode.TvShow.Title,
                            Path.GetFileName(serverFilePath));
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            throw new InvalidCastException("Invalid file template");
        }

        private async Task<Uri> GetCompleteDownloadUri(UnitOfWork unitOfWork, Library? library,
            string resourceDownloadUri)
        {
            
            if (library == null || string.IsNullOrEmpty(library.Server?.LastKnownUri))
                throw new ArgumentException();
            UriBuilder uriBuilder = new UriBuilder(library.Server.LastKnownUri)
            {
                Path = resourceDownloadUri
            };
            return uriBuilder.Uri;
        }

        private async Task<IMediaElement?> GetMediaElement(UnitOfWork unitOfWork, ElementType elementType, string key)
        {
            switch (elementType)
            {
                case ElementType.Movie:
                    return unitOfWork.MovieRepository.Get(x => x.RatingKey == key, includeProperties: nameof(Movie.MediaFiles)).FirstOrDefault();
                case ElementType.TvShow:
                    return unitOfWork.EpisodeRepository.Get(x => x.RatingKey == key, null, nameof(Episode.TvShow)+","+nameof(Episode.MediaFiles))
                        .FirstOrDefault();
                default:
                    return null;
            }
        }

        public async Task DownloadMovie(string key, string mediaFileKey)
        {
            var downloadElement = await CreateDownloadElement(key, mediaFileKey, ElementType.Movie);
            AddToPendingDownloads(downloadElement);
        }

        public async Task DownloadEpisode(string key, string mediaFileKey)
        {
            var downloadElement = await CreateDownloadElement(key, mediaFileKey, ElementType.TvShow);
            AddToPendingDownloads(downloadElement);
        }

        public async Task DownloadSeason(string key, int season)
        {
            using (var scope = _scopeFactory.CreateScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<UnitOfWork>();
                TvShow? tvShow = unitOfWork.TvShowRepository.Get(x => x.RatingKey == key, null, "Episodes")
                    .FirstOrDefault();
                if (tvShow == null)
                    throw new InvalidOperationException();
                var episodes = tvShow.Episodes.Where(x => x.SeasonNumber == season);
                var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                foreach (Episode episode in episodes)
                {
                    var mediaFile = await SelectMediaFile(episode.MediaFiles, settingsService);
                    var downloadElement = await CreateDownloadElement(episode.RatingKey, mediaFile.DownloadUri, ElementType.TvShow);
                    AddToPendingDownloads(downloadElement);
                }
            }
        }

        public async Task DownloadPlaylist(string key)
        {
            using (var scope = _scopeFactory.CreateScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<UnitOfWork>();
                Playlist? playlist = await unitOfWork.PlaylistRepository.GetById(key);
                if (playlist == null)
                    throw new InvalidOperationException();

                IEnumerable<Movie> movies = unitOfWork.MovieRepository.Get(x => playlist.Items.Contains(x.RatingKey));
                IEnumerable<Episode> episodes = unitOfWork.EpisodeRepository.Get(x => playlist.Items.Contains(x.RatingKey));
                
                var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                foreach (Movie movie in movies)
                {
                    var mediaFile = await SelectMediaFile(movie.MediaFiles, settingsService);
                    var downloadElement = await CreateDownloadElement(movie.RatingKey, mediaFile.DownloadUri, ElementType.Movie);
                    AddToPendingDownloads(downloadElement);
                }

                foreach (Episode episode in episodes)
                {
                    var mediaFile = await SelectMediaFile(episode.MediaFiles, settingsService);
                    var downloadElement = await CreateDownloadElement(episode.RatingKey, mediaFile.DownloadUri, ElementType.TvShow);
                    AddToPendingDownloads(downloadElement);
                }
            }
        }

        public async Task DownloadTvShow(string key)
        {
            using (var scope = _scopeFactory.CreateScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<UnitOfWork>();
                TvShow? tvShow = unitOfWork.TvShowRepository.Get(x => x.RatingKey == key, null, "Episodes")
                    .FirstOrDefault();
                if (tvShow == null)
                    throw new InvalidOperationException();
                var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                foreach (Episode episode in tvShow.Episodes)
                {
                    
                    var mediaFile = await SelectMediaFile(episode.MediaFiles, settingsService);
                    var downloadElement = await CreateDownloadElement(episode.RatingKey, mediaFile.DownloadUri,ElementType.TvShow);
                    AddToPendingDownloads(downloadElement);
                }
            }
        }

        public Task CancelDownload(string mediaKey)
        {
            var downloadElement = _pendingDownloads.FirstOrDefault(x => x.MediaKey == mediaKey);
            if (downloadElement != null)
            {
                downloadElement.CancellationTokenSource.Cancel();
                if (downloadElement.Started == null)
                    _pendingDownloads.Remove(downloadElement);
                downloadElement.Finished = DateTimeOffset.Now;
            }

            return Task.CompletedTask;
        }

        private void AddToPendingDownloads(DownloadElement toDownload)
        {
            if (_pendingDownloads.All(x => x.MediaKey != toDownload.MediaKey))
            {
                _logger.LogInformation("Adding new element to download queue: {0}", toDownload.Name);
                _pendingDownloads.Add(toDownload);
                StartDownloaderIfNotActive();
            }
        }

        private void StartDownloaderIfNotActive()
        {
            if (!_isDownloading)
            {
                _isDownloading = true;
                Task.Run(async () => await DownloadQueue());
            }
        }

        private async Task<MediaFile> SelectMediaFile(IEnumerable<MediaFile> mediaFiles, ISettingsService settingsService)
        {
            var preferredResolution = await settingsService.GetPreferredResolution();
            var preferredVideoCodec = await settingsService.GetPreferredVideoCodec();
            List<MediaFile> selection = new List<MediaFile>(mediaFiles);
            if (!string.IsNullOrWhiteSpace(preferredResolution))
            {
                var innerSelection = selection.Where(x =>
                    string.Equals(x.VideoResolution, preferredResolution, StringComparison.OrdinalIgnoreCase)).ToList();
                if (innerSelection.Any())
                    selection = innerSelection;
            }

            if (!string.IsNullOrWhiteSpace(preferredVideoCodec))
            {
                var innerSelection = selection.Where(x =>
                    string.Equals(x.VideoCodec, preferredVideoCodec, StringComparison.OrdinalIgnoreCase)).ToList();
                if (innerSelection.Any())
                    selection = innerSelection;
            }

            return selection.FirstOrDefault();
        }

        private async Task DownloadQueue()
        {
            while (_pendingDownloads.Count > 0)
            {
                _isDownloading = true;
                DownloadElement downloadElement = _pendingDownloads.First();
                _logger.LogInformation("Start download of next element in queue: {0}", downloadElement.Name);

                await Preprocess(downloadElement);
                await DownloadFile(downloadElement);
                await Postprocess(downloadElement);

                _logger.LogInformation("Finished download: {0}", downloadElement.Name);

                _pendingDownloads.RemoveAt(0);
            }

            _logger.LogInformation("No more elements in download queue.");

            _isDownloading = false;
        }

        private async Task Preprocess(DownloadElement downloadElement)
        {
            downloadElement.Started = DateTimeOffset.Now;
        }

        private async Task Postprocess(DownloadElement downloadElement)
        {
            downloadElement.Finished = DateTimeOffset.Now;
            using (var scope = _scopeFactory.CreateScope())
            {
                UnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<UnitOfWork>();
                await unitOfWork.DownloadRepository.Insert(downloadElement);
                await unitOfWork.Save();
            }

            if (!downloadElement.FinishedSuccessfully)
            {
                if (File.Exists(downloadElement.FilePath))
                    File.Delete(downloadElement.FilePath);
            }
        }

        private async Task<HttpResponseMessage> SendDownloadRequest(DownloadElement downloadElement)
        {
            HttpRequestMessage httpRequestMessage = downloadElement.RequestMessage;
            CancellationToken cancellationToken = downloadElement.CancellationTokenSource.Token;
            HttpResponseMessage response = null;
            
            try
            {
                response = await _httpClient.SendAsync(httpRequestMessage, HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                response.EnsureSuccessStatusCode();
                return response;
            }
            catch (HttpRequestException e)
            {
                _logger.LogError(e,
                    "An error occured while trying to access the file to download. As there might be an issue with the selected connection to the plex media server, it  will retry with different connections.");
            }

            IReadOnlyCollection<Uri> availableUris;
            string? accessToken;
            using (var scope = _scopeFactory.CreateScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<UnitOfWork>();
                var server = await unitOfWork.ServerRepository.GetById(downloadElement.ServerId);
                var plexRestService = scope.ServiceProvider.GetRequiredService<PlexRestService>();
                availableUris = plexRestService.GetAllPossibleConnectionUrisForServer(server);
                accessToken = server.AccessToken;
            }

            foreach (Uri uri in availableUris)
            {
                httpRequestMessage = new HttpRequestMessage(HttpMethod.Get, uri);
                httpRequestMessage.Headers.Add("X-Plex-Token", accessToken);
                response = await _httpClient.SendAsync(httpRequestMessage, HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if(response.IsSuccessStatusCode)
                    return response;
            }

            if (response == null)
                throw new InvalidOperationException("Cannot process download response, because there is no response.");

            return response;
        }

        private async Task DownloadFile(DownloadElement downloadElement)
        {
            try
            {
                var response = await SendDownloadRequest(downloadElement);
                response.EnsureSuccessStatusCode();
                var stream = await response.Content.ReadAsStreamAsync();

                var fileInfo = new FileInfo(downloadElement.FilePath);
                fileInfo.Directory.Create();
                using (var fileStream = fileInfo.OpenWrite())
                {
                    await CopyToAsync(stream, fileStream, downloadElement, _resilientStreamPolicy);
                }

                downloadElement.Progress = 1;
                downloadElement.FinishedSuccessfully = true;
            }
            catch (TaskCanceledException)
            {
                _logger.LogInformation("Download of item {0} was cancelled.", downloadElement.Name);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "An error occured while downloading item {0}", downloadElement.Name);
            }
        }
        
        
        
        // private static void TryAddDownload

        private static async Task CopyToAsync(Stream source, Stream destination, DownloadElement downloadElement,
            IAsyncPolicy<int> policy,
            int bufferSize = 0x1000)
        {
            CancellationToken cancellationToken = downloadElement.CancellationTokenSource.Token;
            var buffer = new byte[bufferSize];
            int bytesRead;
            while ((bytesRead =
                       await policy.ExecuteAsync(() => source.ReadAsync(buffer, 0, buffer.Length, cancellationToken))) >
                   0)
            {
                await destination.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
#if DEBUG
                Console.WriteLine(
                    $"Download progress: {downloadElement.DownloadedBytes * 100 / downloadElement.TotalBytes}% - {downloadElement.DownloadedBytes}/{downloadElement.TotalBytes}");
#endif
                downloadElement.DownloadedBytes += bytesRead;
            }
        }

        private static bool AllButIoExceptions(Exception exception)
        {
            if (exception is IOException || exception is TaskCanceledException)
            {
                return false;
            }

            return true;
        }
    }
}