using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using ComposableAsync;
using Jellyfin.Plugin.Danmu.Core.Extensions;
using Jellyfin.Plugin.Danmu.Scrapers.Entity;
using Jellyfin.Plugin.Danmu.Scrapers.Youku.Entity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using RateLimiter;

namespace Jellyfin.Plugin.Danmu.Scrapers.Youku;

public class YoukuApi : AbstractApi
{
    private static readonly object _lock = new object();
    private static readonly Regex yearReg = new Regex(@"[12][890][0-9][0-9]", RegexOptions.Compiled);
    private static readonly Regex moviesReg = new Regex(@"<a.*?h5-show-card.*?>([\w\W]+?)</a>", RegexOptions.Compiled);
    private static readonly Regex trackInfoReg = new Regex(@"data-trackinfo=""(\{[\w\W]+?\})""", RegexOptions.Compiled);
    private static readonly Regex featureReg = new Regex(@"<div.*?show-feature.*?>([\w\W]+?)</div>", RegexOptions.Compiled);
    private static readonly Regex unusedReg = new Regex(@"\[.+?\]|\(.+?\)|【.+?】", RegexOptions.Compiled);

    private DateTime lastRequestTime = DateTime.Now.AddDays(-1);

    private TimeLimiter _timeConstraint = TimeLimiter.GetFromMaxCountByInterval(1, TimeSpan.FromMilliseconds(1000));
    private TimeLimiter _delayExecuteConstraint = TimeLimiter.GetFromMaxCountByInterval(1, TimeSpan.FromMilliseconds(100));

    protected string _cna = string.Empty;
    protected string _token = string.Empty;
    protected string _tokenEnc = string.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="YoukuApi"/> class.
    /// </summary>
    /// <param name="loggerFactory">The <see cref="ILoggerFactory"/>.</param>
    public YoukuApi(ILoggerFactory loggerFactory)
        : base(loggerFactory.CreateLogger<YoukuApi>())
    {
    }


    public async Task<List<YoukuVideo>> SearchAsync(string keyword, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(keyword))
        {
            return new List<YoukuVideo>();
        }

        var cacheKey = $"search_{keyword}";
        var expiredOption = new MemoryCacheEntryOptions() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) };
        if (_memoryCache.TryGetValue<List<YoukuVideo>>(cacheKey, out var cacheValue))
        {
            return cacheValue;
        }

        await this.LimitRequestFrequently();

        keyword = HttpUtility.UrlEncode(keyword);
        var ua = HttpUtility.UrlEncode(AbstractApi.HTTP_USER_AGENT);
        var url = $"https://search.youku.com/api/search?keyword={keyword}&userAgent={ua}&site=1&categories=0&ftype=0&ob=0&pg=1";
        var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var result = new List<YoukuVideo>();
        var searchResult = await response.Content.ReadFromJsonAsync<YoukuSearchResult>(_jsonOptions, cancellationToken).ConfigureAwait(false);
        if (searchResult != null && searchResult.PageComponentList != null)
        {
            foreach (YoukuSearchComponent component in searchResult.PageComponentList)
            {
                if (component.CommonData == null
                || component.CommonData.TitleDTO == null
                || component.CommonData.HasYouku != 1
                || component.CommonData.IsYouku != 1)
                {
                    continue;
                }

                if (component.CommonData.TitleDTO.DisplayName.Contains("中配版"))
                {
                    continue;
                }

                var year = yearReg.FirstMatch(component.CommonData.Feature).ToInt();
                result.Add(new YoukuVideo()
                {
                    ID = component.CommonData.ShowId,
                    Type = component.CommonData.Feature.Contains("电影") ? "movie" : "tv",
                    Year = year > 0 ? year : null,
                    Title = unusedReg.Replace(component.CommonData.TitleDTO.DisplayName, ""),
                    Total = component.CommonData.EpisodeTotal
                });
            }
        }

        _memoryCache.Set<List<YoukuVideo>>(cacheKey, result, expiredOption);
        return result;
    }

    public async Task<YoukuVideo?> GetVideoAsync(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        var cacheKey = $"media_{id}";
        var expiredOption = new MemoryCacheEntryOptions() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) };
        if (_memoryCache.TryGetValue<YoukuVideo?>(cacheKey, out var video))
        {
            return video;
        }

        // 获取影片信息：https://openapi.youku.com/v2/shows/show.json?client_id=53e6cc67237fc59a&package=com.huawei.hwvplayer.youku&show_id=0b39c5b6569311e5b2ad
        // 获取影片剧集信息：https://openapi.youku.com/v2/shows/videos.json?client_id=53e6cc67237fc59a&package=com.huawei.hwvplayer.youku&ext=show&show_id=XMTM1MTc4MDU3Ng==
        var url = $"https://openapi.youku.com/v2/shows/videos.json?client_id=53e6cc67237fc59a&package=com.huawei.hwvplayer.youku&ext=show&show_id={id}";
        var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<YoukuVideo>(_jsonOptions, cancellationToken).ConfigureAwait(false);
        if (result != null)
        {
            _memoryCache.Set<YoukuVideo?>(cacheKey, result, expiredOption);
            return result;
        }

        _memoryCache.Set<YoukuVideo?>(cacheKey, null, expiredOption);
        return null;
    }


    public async Task<YoukuEpisode?> GetEpisodeAsync(string vid, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(vid))
        {
            return null;
        }

        var cacheKey = $"episode_{vid}";
        var expiredOption = new MemoryCacheEntryOptions() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) };
        if (_memoryCache.TryGetValue<YoukuEpisode?>(cacheKey, out var episode))
        {
            return episode;
        }

        // 文档：https://cloud.youku.com/docs?id=46
        var url = $"https://openapi.youku.com/v2/videos/show_basic.json?client_id=53e6cc67237fc59a&package=com.huawei.hwvplayer.youku&video_id={vid}";
        var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<YoukuEpisode>(_jsonOptions, cancellationToken).ConfigureAwait(false);
        if (result != null)
        {
            _memoryCache.Set<YoukuEpisode?>(cacheKey, result, expiredOption);
            return result;
        }

        _memoryCache.Set<YoukuEpisode?>(cacheKey, null, expiredOption);
        return null;
    }

    public async Task<List<YoukuComment>> GetDanmuContentAsync(string vid, CancellationToken cancellationToken)
    {
        var danmuList = new List<YoukuComment>();
        if (string.IsNullOrEmpty(vid))
        {
            return danmuList;
        }

        await EnsureTokenCookie(cancellationToken);


        var episode = await this.GetEpisodeAsync(vid, cancellationToken);
        if (episode == null)
        {
            return danmuList;
        }

        var totalMat = episode.TotalMat;
        for (int mat = 0; mat < totalMat; mat++)
        {
            var comments = await this.GetDanmuContentByMatAsync(vid, mat, cancellationToken);
            danmuList.AddRange(comments);

            // 等待一段时间避免api请求太快
            await _delayExecuteConstraint;
        }

        return danmuList;
    }

    // mat从0开始，视频分钟数
    public async Task<List<YoukuComment>> GetDanmuContentByMatAsync(string vid, int mat, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(vid))
        {
            return new List<YoukuComment>();
        }

        await EnsureTokenCookie(cancellationToken);


        var ctime = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds();
        var msg = new Dictionary<string, object>() {
            {"pid", 0},
            {"ctype", 10004},
            {"sver", "3.1.0"},
            {"cver", "v1.0"},
            {"ctime" , ctime},
            {"guid", this._cna},
            {"vid", vid},
            {"mat", mat},
            {"mcount", 1},
            {"type", 1}
        };

        // 需key按字母排序
        var msgOrdered = msg.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value).ToJson();
        var msgEnc = Convert.ToBase64String(Encoding.UTF8.GetBytes(msgOrdered));
        var sign = this.generateMsgSign(msgEnc);
        msg.Add("msg", msgEnc);
        msg.Add("sign", sign);


        var appKey = "24679788";
        var data = msg.ToJson();
        var t = Convert.ToString(new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds());
        var param = HttpUtility.ParseQueryString(string.Empty);
        param["jsv"] = "2.7.0";
        param["appKey"] = appKey;
        param["t"] = t;
        param["sign"] = this.generateTokenSign(t, appKey, data);
        param["api"] = "mopen.youku.danmu.list";
        param["v"] = "1.0";
        param["type"] = "originaljson";
        param["dataType"] = "jsonp";
        param["timeout"] = "20000";
        param["jsonpIncPrefix"] = "utility";

        var builder = new UriBuilder("https://acs.youku.com/h5/mopen.youku.danmu.list/1.0/");
        builder.Query = param.ToString();
        HttpResponseMessage response;
        var formContent = new FormUrlEncodedContent(new[]{
                new KeyValuePair<string, string>("data", data)
        });
        using (var requestMessage = new HttpRequestMessage(HttpMethod.
        Post, builder.Uri.ToString())
        { Content = formContent })
        {
            requestMessage.Headers.Add("Referer", "https://v.youku.com");

            response = await httpClient.SendAsync(requestMessage);
        }
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<YoukuRpcResult>(_jsonOptions, cancellationToken).ConfigureAwait(false);
        if (result != null && !string.IsNullOrEmpty(result.Data.Result))
        {
            var commentResult = JsonSerializer.Deserialize<YoukuCommentResult>(result.Data.Result);
            if (commentResult != null && commentResult.Data != null)
            {
                // 每段有60秒弹幕，为避免弹幕太大，从中间隔抽取最大60秒200条弹幕
                return commentResult.Data.Result.ExtractToNumber(200).ToList();
            }
        }

        return new List<YoukuComment>();
    }

    protected async Task LimitRequestFrequently()
    {
        await this._timeConstraint;
    }

    protected async Task EnsureTokenCookie(CancellationToken cancellationToken)
    {
        var cookies = _cookieContainer.GetCookies(new Uri("https://mmstat.com", UriKind.Absolute));
        var cookie = cookies.FirstOrDefault(x => x.Name == "cna");
        if (cookie == null)
        {
            var url = "https://log.mmstat.com/eg.js";
            var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            // 重新读取最新
            cookies = _cookieContainer.GetCookies(new Uri("https://mmstat.com", UriKind.Absolute));
            cookie = cookies.FirstOrDefault(x => x.Name == "cna");
        }
        if (cookie != null)
        {
            _cna = cookie.Value;
        }


        cookies = _cookieContainer.GetCookies(new Uri("https://youku.com", UriKind.Absolute));
        var tokenCookie = cookies.FirstOrDefault(x => x.Name == "_m_h5_tk");
        var tokenEncCookie = cookies.FirstOrDefault(x => x.Name == "_m_h5_tk_enc");
        if (tokenCookie == null || tokenEncCookie == null)
        {
            var url = "https://acs.youku.com/h5/mtop.com.youku.aplatform.weakget/1.0/?jsv=2.5.1&appKey=24679788";
            var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            // 重新读取最新
            cookies = _cookieContainer.GetCookies(new Uri("https://youku.com", UriKind.Absolute));
            tokenCookie = cookies.FirstOrDefault(x => x.Name == "_m_h5_tk");
            tokenEncCookie = cookies.FirstOrDefault(x => x.Name == "_m_h5_tk_enc");
        }

        if (tokenCookie != null)
        {
            _token = tokenCookie.Value;
        }
        if (tokenEncCookie != null)
        {
            _tokenEnc = tokenEncCookie.Value;
        }
    }


    protected string generateMsgSign(string msgEnc)
    {
        return (msgEnc + "MkmC9SoIw6xCkSKHhJ7b5D2r51kBiREr").ToMD5().ToLower();
    }

    protected string generateTokenSign(string t, string appKey, string data)
    {
        var arr = new string[] { this._token.Substring(0, 32), t, appKey, data };
        return string.Join('&', arr).ToMD5().ToLower();
    }
}

