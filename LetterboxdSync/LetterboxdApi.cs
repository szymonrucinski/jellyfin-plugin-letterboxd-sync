using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HtmlAgilityPack;

namespace LetterboxdSync;

public class LetterboxdApi
{
    private string cookie = string.Empty;
    private string csrf = string.Empty;

    private string username = string.Empty;

    public async Task Authenticate(string username, string password)
    {
        string url = "https://letterboxd.com/user/login.do";

        var cookieContainer = new CookieContainer();
        this.username = username;

        using (var client = new HttpClient(new HttpClientHandler { CookieContainer = cookieContainer }))
        {
            var response = await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string> { })).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new Exception($"Letterbox return {(int)response.StatusCode}");

            this.cookie = CookieToString(cookieContainer.GetCookies(new Uri(url)));
            using (JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)))
                this.csrf = GetElementFromJson(doc.RootElement, "csrf");
        }

        using (var client = new HttpClient(new HttpClientHandler { CookieContainer = cookieContainer }))
        {
            client.DefaultRequestHeaders.Add("DNT", "1");
            client.DefaultRequestHeaders.Add("Host", "letterboxd.com");
            client.DefaultRequestHeaders.Add("Origin", "https://letterboxd.com");
            client.DefaultRequestHeaders.Add("Priority", "u=0");
            client.DefaultRequestHeaders.Add("Referer", "https://letterboxd.com/");
            client.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "empty");
            client.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "cors");
            client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
            client.DefaultRequestHeaders.Add("Sec-GPC", "1");
            client.DefaultRequestHeaders.Add("TE", "trailers");
            client.DefaultRequestHeaders.Add("Cookie", this.cookie);

            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "username", username },
                { "password", password },
                { "__csrf", this.csrf },
                { "authenticationCode", " " }
            });

            var response = await client.PostAsync(url, content).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new Exception($"Letterbox return {(int)response.StatusCode}");

            this.cookie = CookieToString(cookieContainer.GetCookies(new Uri(url)));

            using (JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)))
            {
                var json = doc.RootElement;
                if (SucessOperation(json, out string message))
                    this.csrf = GetElementFromJson(doc.RootElement, "csrf");
                else
                    throw new Exception(message);
            }
        }
    }

    public async Task<FilmResult> SearchFilmByTmdbId(int tmdbid)
    {
        string tmdbUrl = $"https://letterboxd.com/tmdb/{tmdbid}";

        var handler = new HttpClientHandler()
        {
            AllowAutoRedirect = true
        };

        using (var client = new HttpClient(handler))
        {
            var res = await client.GetAsync(tmdbUrl).ConfigureAwait(false);

            string letterboxdUrl = res?.RequestMessage?.RequestUri?.ToString();
            var filmSlugRegex = Regex.Match(letterboxdUrl, @"https:\/\/letterboxd\.com\/film\/([^\/]+)\/");

            string filmSlug = filmSlugRegex.Groups[1].Value;
            if (string.IsNullOrEmpty(filmSlug))
                throw new Exception("The search returned no results");

            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(await res.Content.ReadAsStringAsync().ConfigureAwait(false));

            var span = htmlDoc.DocumentNode.SelectSingleNode("//div[@data-item-slug='" + filmSlug + "']");
            if (span == null)
                throw new Exception("The search returned no results");

            string filmId = span.GetAttributeValue("data-film-id", string.Empty);
            if (string.IsNullOrEmpty(filmId))
                throw new Exception("The search returned no results");

            return new FilmResult(filmSlug, filmId);
        }
    }

    public async Task MarkAsWatched(string filmId, DateTime? date, string[] tags, bool liked = false)
    {
        string url = $"https://letterboxd.com/s/save-diary-entry";
        DateTime viewingDate = date == null ? DateTime.Now : (DateTime) date;

        using (var client = new HttpClient())
        {
            client.DefaultRequestHeaders.Add("Cookie", this.cookie);

            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "__csrf", this.csrf },
                { "json", "true" },
                { "viewingId", string.Empty },
                { "filmId", filmId },
                { "specifiedDate", date == null ? "false" : "true" },
                { "viewingDateStr", viewingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                { "review", string.Empty },
                { "tags", date != null && tags.Length > 0 ? $"[{string.Join(",", tags)}]" : string.Empty },
                { "rating", "0" },
                { "liked", liked.ToString() }
            });

            var response = await client.PostAsync(url, content).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new Exception($"Letterbox return {(int)response.StatusCode}");

            using (JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)))
            {
                if (!SucessOperation(doc.RootElement, out string message))
                    throw new Exception(message);
            }
        }
    }

    public async Task<DateTime?> GetDateLastLog(string filmSlug)
    {
        string url = $"https://letterboxd.com/{this.username}/film/{filmSlug}/diary/";

        using (var client = new HttpClient())
        {
            client.DefaultRequestHeaders.Add("Cookie", this.cookie);
            var lstDates = new List<DateTime>();

            var response = await client.GetStringAsync(url).ConfigureAwait(false);

            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(response);

            var trReviews = htmlDoc.DocumentNode.SelectNodes("//tr[contains(@class, 'diary-entry-row')]");
            if (trReviews == null)
                return null;

            string currentMonth = string.Empty;
            string currentYear = string.Empty;

            foreach (var trReview in trReviews)
            {
                // Get month and year from the monthdate column (only first row of each month has it)
                var monthNode = trReview.SelectSingleNode(".//td[contains(@class, 'col-monthdate')]//a[@class='month']");
                var yearNode = trReview.SelectSingleNode(".//td[contains(@class, 'col-monthdate')]//a[@class='year']");

                if (monthNode != null)
                    currentMonth = monthNode.InnerText.Trim();
                if (yearNode != null)
                    currentYear = yearNode.InnerText.Trim();

                // Get day from the daydate column
                var dayNode = trReview.SelectSingleNode(".//td[contains(@class, 'col-daydate')]//a[@class='daydate']");
                if (dayNode == null)
                    continue;

                string day = dayNode.InnerText.Trim();

                if (!string.IsNullOrEmpty(currentMonth) && !string.IsNullOrEmpty(currentYear) && !string.IsNullOrEmpty(day))
                {
                    string dateStr = $"{day} {currentMonth} {currentYear}";
                    if (DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsedDate))
                    {
                        lstDates.Add(parsedDate);
                    }
                }
            }

            return lstDates.Count > 0 ? lstDates.Max() : null;
        }
    }

    private string CookieToString(CookieCollection cookies)
    {
        StringBuilder cookieString = new StringBuilder();
        foreach (Cookie cookie in cookies)
        {
            cookieString.Append(new CultureInfo("en-US"), $"{cookie.Name}={cookie.Value}");
            cookieString.Append("; ");
        }

        return cookieString.ToString();
    }

    private string GetElementFromJson(JsonElement json, string property)
    {
        if (json.TryGetProperty(property, out JsonElement element))
            return element.GetString() ?? string.Empty;
        return string.Empty;
    }

    private bool SucessOperation(JsonElement json, out string message)
    {
        message = string.Empty;

        if (json.TryGetProperty("messages", out JsonElement messagesElement))
        {
            StringBuilder erroMessages = new StringBuilder();
            foreach (var i in messagesElement.EnumerateArray())
                erroMessages.Append(i.GetString());
            message = erroMessages.ToString();
        }

        if (json.TryGetProperty("result", out JsonElement statusElement))
        {
            switch (statusElement.ValueKind)
            {
                case JsonValueKind.String:
                    return statusElement.GetString() == "error" ? false : true;
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
            }
        }

        return false;
    }
}

public class FilmResult {
    public string filmSlug = string.Empty;
    public string filmId = string.Empty;

    public FilmResult(string filmSlug, string filmId){
        this.filmSlug = filmSlug;
        this.filmId = filmId;
    }
}
