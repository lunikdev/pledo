﻿using Microsoft.AspNetCore.Mvc;
using Web.Models;
using Web.Models.DTO;
using Web.Services;

namespace Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DownloadController : ControllerBase
{
    private readonly IDownloadService _downloadService;
    private readonly ILogger<AccountController> _logger;

    public DownloadController(IDownloadService downloadService, ILogger<AccountController> logger)
    {
        _downloadService = downloadService;
        _logger = logger;
    }
    
    [HttpGet]
    public async Task<IEnumerable<DownloadElementResource>> GetAll()
    {
        return _downloadService.GetPendingDownloads().Select(ToDownloadElementResource);
    }
    
    [HttpGet("pending")]
    public async Task<IEnumerable<DownloadElementResource>> GetPendingDownloads()
    {
        return _downloadService.GetPendingDownloads().Select(ToDownloadElementResource);
    }

    [HttpPost("movie/{key}")]
    public async Task DownloadMovie( string key)
    {
        await _downloadService.DownloadMovie(key);
    }
    
    [HttpPost("episode/{key}")]
    public async Task DownloadEpisode( string key)
    {
        await _downloadService.DownloadEpisode(key);
    }

    private static DownloadElementResource ToDownloadElementResource(DownloadElement x)
    {
        return new DownloadElementResource()
        {
            Finished = x.Finished,
            Id = x.Id,
            Name = x.Name,
            Progress = x.Progress,
            Started = x.Started,
            Uri = x.Uri,
            DownloadedBytes = x.DownloadedBytes,
            ElementType = x.ElementType,
            FileName = x.FileName,
            FilePath = x.FilePath,
            FinishedSuccessfully = x.FinishedSuccessfully,
            TotalBytes = x.TotalBytes
        };
    }
}