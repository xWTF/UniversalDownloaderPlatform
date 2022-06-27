﻿using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;
using NLog;
using UniversalDownloaderPlatform.Common.Enums;
using UniversalDownloaderPlatform.Common.Exceptions;
using UniversalDownloaderPlatform.Common.Helpers;
using UniversalDownloaderPlatform.Common.Interfaces;
using UniversalDownloaderPlatform.Common.Interfaces.Models;
using UniversalDownloaderPlatform.DefaultImplementations.Interfaces;

namespace UniversalDownloaderPlatform.DefaultImplementations
{
    //TODO: Make disposable?
    public class WebDownloader : IWebDownloader
    {
        private HttpClient _httpClient;
        private HttpClientHandler _httpClientHandler;
        private readonly IRemoteFileSizeChecker _remoteFileSizeChecker;
        private readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private int _maxRetries;
        private int _retryMultiplier;
        private RemoteFileSizeNotAvailableAction _remoteFileSizeNotAvailableAction;
        private readonly Version _httpVersion = HttpVersion.Version20;

        public WebDownloader(IRemoteFileSizeChecker remoteFileSizeChecker)
        {
            _remoteFileSizeChecker =
                remoteFileSizeChecker ?? throw new ArgumentNullException(nameof(remoteFileSizeChecker));
        }

        public virtual async Task BeforeStart(IUniversalDownloaderPlatformSettings settings)
        {
            _maxRetries = settings.MaxDownloadRetries;
            _retryMultiplier = settings.RetryMultiplier;
            _remoteFileSizeNotAvailableAction = settings.RemoteFileSizeNotAvailableAction;

            _httpClientHandler = new HttpClientHandler();
            if (settings.CookieContainer != null)
            {
                _httpClientHandler.UseCookies = true;
                _httpClientHandler.CookieContainer = settings.CookieContainer;
                if(settings.CookieContainer.PerDomainCapacity == CookieContainer.DefaultPerDomainCookieLimit || settings.CookieContainer.Capacity == CookieContainer.DefaultCookieLimit)
                    _logger.Warn("[Developer warning] CookieContainer passed in IUniversalDownloaderPlatformSettings uses default cookie capacity settings. This might result in incorrect cookie management behavior. Consider tuning DefaultPerDomainCookieLimit and Capacity values if you encounter issues.");
            }

            if (!string.IsNullOrWhiteSpace(settings.ProxyServerAddress))
                _httpClientHandler.Proxy = new WebProxy(settings.ProxyServerAddress);

            _httpClient = new HttpClient(_httpClientHandler);
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(settings.UserAgent);

            await _remoteFileSizeChecker.BeforeStart(settings);
        }

        public virtual async Task DownloadFile(string url, string path, string refererUrl = null, bool overwrite = false)
        {
            if(string.IsNullOrEmpty(url))
                throw new ArgumentException("Argument cannot be null or empty", nameof(url));
            if(string.IsNullOrEmpty(path))
                throw new ArgumentException("Argument cannot be null or empty", nameof(path));

            await DownloadFileInternal(url, path, refererUrl, overwrite);
        }

        public virtual void UpdateCookies(CookieCollection cookieCollection)
        {
            if (!_httpClientHandler.UseCookies)
                throw new InvalidOperationException("Cookies are not enabled");

            foreach (Cookie cookie in cookieCollection)
            {
                _httpClientHandler.CookieContainer.Add(cookie);
            }
        }

        private async Task DownloadFileInternal(string url, string path, string refererUrl, bool overwrite, int retry = 0, int retryTooManyRequests = 0)
        {
            if (retry > 0)
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch (Exception fileDeleteException)
                {
                    throw new DownloadException($"Unable to delete corrupted file {path}", fileDeleteException);
                }

                if (retry >= _maxRetries)
                {
                    throw new DownloadException("Retries limit reached");
                }

                await Task.Delay(retry * _retryMultiplier * 1000);
            }

            if(retryTooManyRequests > 0)
                await Task.Delay(retryTooManyRequests * _retryMultiplier * 1000);

            long remoteFileSize = -1;
            bool isFilesIdentical = false;
            try
            {
                remoteFileSize = await _remoteFileSizeChecker.GetRemoteFileSize(url);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Unable to retrieve remote file size, size check will be skipped: {ex}");
                isFilesIdentical = true;
            }

            if (File.Exists(path))
            {
                if (remoteFileSize > 0)
                {
                    _logger.Debug($"File {path} exists, size will be checked");
                    try
                    {
                        FileInfo fileInfo = new FileInfo(path);
                        long fileSize = fileInfo.Length;

                        if (fileSize != remoteFileSize)
                        {
                            string backupFilename =
                                    $"{Path.GetFileNameWithoutExtension(path)}_old_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}{Path.GetExtension(path)}";
                            _logger.Warn($"Local and remote file sizes does not match, file {url} will be redownloaded. Old file will be backed up as {backupFilename}. Remote file size: {remoteFileSize}, local file size: {fileSize}");
                            File.Move(path, Path.Combine(fileInfo.DirectoryName, backupFilename));
                        }
                        else
                        {
                            _logger.Debug($"File size for {path} matches");
                            isFilesIdentical = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, $"Error during file comparison: {ex}");
                        isFilesIdentical = true; //we assume that local file is identical if we can't check remote file size
                    }
                }
                else
                {
                    if (_remoteFileSizeNotAvailableAction == RemoteFileSizeNotAvailableAction.KeepExisting)
                        isFilesIdentical = true;

                    //todo: implement in future versions
                    /*if (_remoteFileSizeNotAvailableAction == RemoteFileSizeNotAvailableAction.DownloadAndCompare)
                    {
                        //download and compare
                    }*/
                }

                if (isFilesIdentical)
                {
                    if (!overwrite)
                    {
                        _logger.Warn($"File {path} already exists and has the same file size as remote file (or remote file is not available). Skipping...");
                        return;
                    }
                    else
                        _logger.Warn($"File {path} already exists, will be overwriten!");
                }
            }

            try
            {
                //warning: returns '' in drive's root
                if (!Directory.Exists(path))
                    Directory.CreateDirectory(new FileInfo(path).DirectoryName);
            }
            catch (Exception ex)
            {
                throw new DownloadException($"Unable to create directory for file {path}", ex);
            }

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url) {Version = _httpVersion})
                {
                    if (!string.IsNullOrWhiteSpace(refererUrl))
                    {
                        try
                        {
                            request.Headers.Referrer = new Uri(refererUrl);
                        }
                        catch (UriFormatException ex)
                        {
                            _logger.Error(ex, $"Invalid referer url: {refererUrl}. Error: {ex}");
                        }
                    }

                    using (HttpResponseMessage responseMessage =
                        await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
                    {
                        if (!responseMessage.IsSuccessStatusCode)
                        {
                            switch (responseMessage.StatusCode)
                            {
                                //todo: configure via config?
                                case HttpStatusCode.BadRequest:
                                case HttpStatusCode.Unauthorized:
                                case HttpStatusCode.Forbidden:
                                case HttpStatusCode.NotFound:
                                case HttpStatusCode.MethodNotAllowed:
                                case HttpStatusCode.Gone:
                                    throw new DownloadException(
                                        $"Error status code returned: {responseMessage.StatusCode}",
                                        responseMessage.StatusCode, await responseMessage.Content.ReadAsStringAsync());
                                case HttpStatusCode.Moved:
                                case HttpStatusCode.Found:
                                case HttpStatusCode.SeeOther:
                                case HttpStatusCode.TemporaryRedirect:
                                case HttpStatusCode.PermanentRedirect:
                                    string newLocation = responseMessage.Headers.Location.ToString();
                                    _logger.Debug(
                                        $"{url} has been moved to: {newLocation}, retrying using new url");
                                    await DownloadFileInternal(newLocation, refererUrl, path, overwrite);
                                    return;
                                case HttpStatusCode.TooManyRequests:
                                    retryTooManyRequests++;
                                    _logger.Debug(
                                        $"Too many requests for {url}, waiting for {retryTooManyRequests * _retryMultiplier} seconds...");
                                    await DownloadFileInternal(url, path, refererUrl, overwrite, 0, retryTooManyRequests);
                                    return;
                            }

                            retry++;

                            _logger.Debug(
                                $"{url} returned status code {responseMessage.StatusCode}, retrying in {retry * _retryMultiplier} seconds ({_maxRetries - retry} retries left)...");
                            await DownloadFileInternal(url, path, refererUrl, overwrite, retry);
                            return;
                        }

                        _logger.Debug($"Starting download: {url}");
                        using (Stream contentStream = await responseMessage.Content.ReadAsStreamAsync(),
                            stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
                                true))
                        {
                            await contentStream.CopyToAsync(stream, 4096);
                        }

                        _logger.Debug($"Finished download: {url}");

                        FileInfo fileInfo = new FileInfo(path);
                        long fileSize = fileInfo.Length;
                        fileInfo = null;

                        if (remoteFileSize > 0 && fileSize != remoteFileSize)
                        {
                            _logger.Warn(
                                $"Downloaded file size differs from the size returned by server. Local size: {fileSize}, remote size: {remoteFileSize}. File {url} will be redownloaded.");

                            File.Delete(path);

                            retry++;

                            await DownloadFileInternal(url, path, refererUrl, overwrite, retry);
                            return;
                        }

                        _logger.Debug($"File size check passed for: {url}");
                    }
                }
            }
            catch (TaskCanceledException ex)
            {
                retry++;
                _logger.Debug(ex,
                    $"Encountered error while trying to download {url}, retrying in {retry * _retryMultiplier} seconds ({_maxRetries - retry} retries left)... The error is: {ex}");
                await DownloadFileInternal(url, path, refererUrl, overwrite, retry);
            }
            catch (IOException ex) when (!(ex is DirectoryNotFoundException))
            {
                retry++;
                _logger.Debug(ex,
                    $"Encountered IO error while trying to access {url}, retrying in {retry * _retryMultiplier} seconds ({_maxRetries - retry} retries left)... The error is: {ex}");
                await DownloadFileInternal(url, path, refererUrl, overwrite, retry);
            }
            catch (SocketException ex)
            {
                retry++;
                _logger.Debug(ex,
                    $"Encountered connection error while trying to access {url}, retrying in {retry * _retryMultiplier} seconds ({_maxRetries - retry} retries left)... The error is: {ex}");
                await DownloadFileInternal(url, path, refererUrl, overwrite, retry);
            }
            catch (DownloadException ex)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new DownloadException($"Unable to download from {url}: {ex.Message}", ex);
            }
        }

        public virtual async Task<string> DownloadString(string url, string refererUrl = null)
        {
            if(string.IsNullOrEmpty(url))
                throw new ArgumentException("Argument cannot be null or empty", nameof(url));

            return await DownloadStringInternal(url, refererUrl);
        }

        private async Task<string> DownloadStringInternal(string url, string refererUrl, int retry = 0, int retryTooManyRequests = 0)
        {
            if (retry > 0)
            {
                if (retry >= _maxRetries)
                {
                    throw new DownloadException("Retries limit reached");
                }

                await Task.Delay(retry * _retryMultiplier * 1000);
            }

            if (retryTooManyRequests > 0)
                await Task.Delay(retryTooManyRequests * _retryMultiplier * 1000);

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url) {Version = _httpVersion})
                {
                    //Add some additional headers to better mimic a real browser
                    request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
                    request.Headers.Add("Accept-Language", "en-US,en;q=0.5");
                    request.Headers.Add("Cache-Control", "no-cache");
                    request.Headers.Add("DNT", "1");

                    if (!string.IsNullOrWhiteSpace(refererUrl))
                    {
                        try
                        {
                            request.Headers.Referrer = new Uri(refererUrl);
                        }
                        catch (UriFormatException ex)
                        {
                            _logger.Error($"Invalid referer url: {refererUrl}");
                        }
                    }

                    using (HttpResponseMessage responseMessage =
                        await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
                    {
                        if (!responseMessage.IsSuccessStatusCode)
                        {
                            switch (responseMessage.StatusCode)
                            {
                                case HttpStatusCode.BadRequest:
                                case HttpStatusCode.Unauthorized:
                                case HttpStatusCode.Forbidden:
                                case HttpStatusCode.NotFound:
                                case HttpStatusCode.MethodNotAllowed:
                                case HttpStatusCode.Gone:
                                    throw new DownloadException($"Error status code returned: {responseMessage.StatusCode}", 
                                        responseMessage.StatusCode, await responseMessage.Content.ReadAsStringAsync());
                                case HttpStatusCode.Moved:
                                case HttpStatusCode.Found:
                                case HttpStatusCode.SeeOther:
                                case HttpStatusCode.TemporaryRedirect:
                                case HttpStatusCode.PermanentRedirect:
                                    string newLocation = responseMessage.Headers.Location.ToString();
                                    _logger.Debug(
                                        $"{url} has been moved to: {newLocation}, retrying using new url");
                                    return await DownloadStringInternal(newLocation, refererUrl);
                                case HttpStatusCode.TooManyRequests:
                                    retryTooManyRequests++;
                                    _logger.Debug(
                                        $"Too many requests for {url}, waiting for {retryTooManyRequests * _retryMultiplier} seconds...");
                                    return await DownloadStringInternal(url, refererUrl, 0, retryTooManyRequests);
                            }

                            retry++;

                            _logger.Debug(
                                $"{url} returned status code {responseMessage.StatusCode}, retrying in {retry * _retryMultiplier} seconds ({_maxRetries - retry} retries left)...");
                            return await DownloadStringInternal(url, refererUrl, retry);
                        }

                        return await responseMessage.Content.ReadAsStringAsync();
                    }
                }
            }
            catch (TaskCanceledException ex)
            {
                retry++;
                _logger.Debug(ex,
                    $"Encountered timeout error while trying to access {url}, retrying in {retry * _retryMultiplier} seconds ({_maxRetries - retry} retries left)... The error is: {ex}");
                return await DownloadStringInternal(url, refererUrl, retry);
            }
            catch (IOException ex)
            {
                retry++;
                _logger.Debug(ex,
                    $"Encountered IO error while trying to access {url}, retrying in {retry * _retryMultiplier} seconds ({_maxRetries - retry} retries left)... The error is: {ex}");
                return await DownloadStringInternal(url, refererUrl, retry);
            }
            catch (SocketException ex)
            {
                retry++;
                _logger.Debug(ex,
                    $"Encountered connection error while trying to access {url}, retrying in {retry * _retryMultiplier} seconds ({_maxRetries - retry} retries left)... The error is: {ex}");
                return await DownloadStringInternal(url, refererUrl, retry);
            }
            catch (DownloadException ex)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new DownloadException($"Unable to retrieve data from {url}: {ex.Message}", ex);
            }
        }
    }
}
