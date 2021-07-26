using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using FluentValidation;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers.Exceptions;
using NzbDrone.Core.Indexers.Gazelle;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Indexers.Definitions
{
    public class SecretCinema : TorrentIndexerBase<SecretCinemaSettings>
    {
        public override string Name => "Secret Cinema";
        public override string[] IndexerUrls => new string[] { "https://secret-cinema.pw/" };
        protected virtual string LoginUrl => Settings.BaseUrl + "login.php";
        public override string Description => "A tracker for rare movies.";
        public override DownloadProtocol Protocol => DownloadProtocol.Torrent;
        public override IndexerPrivacy Privacy => IndexerPrivacy.Private;
        public override IndexerCapabilities Capabilities => SetCapabilities();
        public override bool SupportsRedirect => true;

        public SecretCinema(IHttpClient httpClient, IEventAggregator eventAggregator, IIndexerStatusService indexerStatusService, IConfigService configService, Logger logger)
            : base(httpClient, eventAggregator, indexerStatusService, configService, logger)
        {
        }

        public override IIndexerRequestGenerator GetRequestGenerator()
        {
            return new SecretCinemaRequestGenerator() { Settings = Settings, Capabilities = Capabilities, HttpClient = _httpClient };
        }

        public override IParseIndexerResponse GetParser()
        {
            return new SecretCinemaParser(Settings, Capabilities.Categories);
        }

        private IndexerCapabilities SetCapabilities()
        {
            var caps = new IndexerCapabilities
            {
                MovieSearchParams = new List<MovieSearchParam>
                       {
                           MovieSearchParam.Q
                       },
                MusicSearchParams = new List<MusicSearchParam>
                       {
                           MusicSearchParam.Q, MusicSearchParam.Album, MusicSearchParam.Artist, MusicSearchParam.Label, MusicSearchParam.Year
                       }
            };

            caps.Categories.AddCategoryMapping(1, NewznabStandardCategory.Movies, "Movies");
            caps.Categories.AddCategoryMapping(2, NewznabStandardCategory.Audio, "Music");

            return caps;
        }
    }

    public class SecretCinemaRequestGenerator : IIndexerRequestGenerator
    {
        public SecretCinemaSettings Settings { get; set; }
        public IndexerCapabilities Capabilities { get; set; }
        public Func<IDictionary<string, string>> GetCookies { get; set; }
        public Action<IDictionary<string, string>, DateTime?> CookiesUpdater { get; set; }
        public IHttpClient HttpClient { get; set; }

        public SecretCinemaRequestGenerator()
        {
        }

        public IndexerPageableRequestChain GetSearchRequests(MusicSearchCriteria searchCriteria)
        {
            var pageableRequests = new IndexerPageableRequestChain();

            pageableRequests.Add(GetRequest(string.Format("&artistname={0}&groupname={1}", searchCriteria.Artist, searchCriteria.Album)));

            return pageableRequests;
        }

        public IndexerPageableRequestChain GetSearchRequests(BookSearchCriteria searchCriteria)
        {
            var pageableRequests = new IndexerPageableRequestChain();

            pageableRequests.Add(GetRequest(searchCriteria.SanitizedSearchTerm));

            return pageableRequests;
        }

        public IndexerPageableRequestChain GetSearchRequests(MovieSearchCriteria searchCriteria)
        {
            return new IndexerPageableRequestChain();
        }

        public IndexerPageableRequestChain GetSearchRequests(TvSearchCriteria searchCriteria)
        {
            return new IndexerPageableRequestChain();
        }

        public IndexerPageableRequestChain GetSearchRequests(BasicSearchCriteria searchCriteria)
        {
            var pageableRequests = new IndexerPageableRequestChain();

            pageableRequests.Add(GetRequest(searchCriteria.SanitizedSearchTerm));

            return pageableRequests;
        }

        private IEnumerable<IndexerRequest> GetRequest(string searchParameters)
        {
            var req = RequestBuilder()
                .Resource($"ajax.php?action=browse&searchstr={searchParameters}")
                .Build();

            yield return new IndexerRequest(req);
        }

        private HttpRequestBuilder RequestBuilder()
        {
            return new HttpRequestBuilder($"{Settings.BaseUrl.Trim().TrimEnd('/')}")
                .Accept(HttpAccept.Json);
        }
    }

    public class SecretCinemaParser : IParseIndexerResponse
    {
        private readonly SecretCinemaSettings _settings;
        private readonly IndexerCapabilitiesCategories _categories;
        public Action<IDictionary<string, string>, DateTime?> CookiesUpdater { get; set; }

        public SecretCinemaParser(SecretCinemaSettings settings, IndexerCapabilitiesCategories categories)
        {
            _settings = settings;
            _categories = categories;
        }

        public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
            var torrentInfos = new List<ReleaseInfo>();

            if (indexerResponse.HttpResponse.StatusCode != HttpStatusCode.OK)
            {
                throw new IndexerException(indexerResponse, $"Unexpected response status {indexerResponse.HttpResponse.StatusCode} code from API request");
            }

            if (!indexerResponse.HttpResponse.Headers.ContentType.Contains(HttpAccept.Json.Value))
            {
                throw new IndexerException(indexerResponse, $"Unexpected response header {indexerResponse.HttpResponse.Headers.ContentType} from API request, expected {HttpAccept.Json.Value}");
            }

            var jsonResponse = new HttpResponse<GazelleResponse>(indexerResponse.HttpResponse);
            if (jsonResponse.Resource.Status != "success" ||
                string.IsNullOrWhiteSpace(jsonResponse.Resource.Status) ||
                jsonResponse.Resource.Response == null)
            {
                return torrentInfos;
            }

            foreach (var result in jsonResponse.Resource.Response.Results)
            {
                if (result.Torrents != null)
                {
                    foreach (var torrent in result.Torrents)
                    {
                        var id = torrent.TorrentId;
                        var artist = WebUtility.HtmlDecode(result.Artist);
                        var album = WebUtility.HtmlDecode(result.GroupName);

                        var title = $"{result.GroupName} ({result.GroupYear}) [{torrent.Format} {torrent.Encoding}] [{torrent.Media}]";
                        if (torrent.HasCue)
                        {
                            title += " [Cue]";
                        }

                        GazelleInfo release = new GazelleInfo()
                        {
                            //Guid = string.Format("SecretCinema-{0}", id),

                            // Splice Title from info to avoid calling API again for every torrent.
                            Title = WebUtility.HtmlDecode(title),

                            Container = torrent.Encoding,
                            Codec = torrent.Format,
                            Size = long.Parse(torrent.Size),
                            DownloadUrl = GetDownloadUrl(id, torrent.CanUseToken),
                            InfoUrl = GetInfoUrl(result.GroupId, id),
                            Seeders = int.Parse(torrent.Seeders),
                            Peers = int.Parse(torrent.Leechers) + int.Parse(torrent.Seeders),
                            PublishDate = torrent.Time.ToUniversalTime(),
                            Scene = torrent.Scene,
                            Freeleech = torrent.IsFreeLeech || torrent.IsPersonalFreeLeech,
                            Files = torrent.FileCount,
                            Grabs = torrent.Snatches,
                        };

                        var category = torrent.Category;
                        if (category == null || category.Contains("Select Category"))
                        {
                            release.Categories = _categories.MapTrackerCatToNewznab("1");
                        }
                        else
                        {
                            release.Categories = _categories.MapTrackerCatDescToNewznab(category);
                        }

                        torrentInfos.Add(release);
                    }
                }

                // Non-Audio files are formatted a little differently (1:1 for group and torrents)
                else
                {
                    var id = result.TorrentId;
                    GazelleInfo release = new GazelleInfo()
                    {
                        Guid = string.Format("SecretCinema-{0}", id),
                        Title = WebUtility.HtmlDecode(result.GroupName),
                        Size = long.Parse(result.Size),
                        DownloadUrl = GetDownloadUrl(id, result.CanUseToken),
                        InfoUrl = GetInfoUrl(result.GroupId, id),
                        Seeders = int.Parse(result.Seeders),
                        Peers = int.Parse(result.Leechers) + int.Parse(result.Seeders),
                        PublishDate = DateTimeOffset.FromUnixTimeSeconds(result.GroupTime).UtcDateTime,
                        Freeleech = result.IsFreeLeech || result.IsPersonalFreeLeech,
                        Files = result.FileCount,
                        Grabs = result.Snatches,
                    };

                    var category = result.Category;
                    if (category == null || category.Contains("Select Category"))
                    {
                        release.Categories = _categories.MapTrackerCatToNewznab("1");
                    }
                    else
                    {
                        release.Categories = _categories.MapTrackerCatDescToNewznab(category);
                    }

                    torrentInfos.Add(release);
                }
            }

            // order by date
            return
                torrentInfos
                    .OrderByDescending(o => o.PublishDate)
                    .ToArray();
        }

        private string GetDownloadUrl(int torrentId, bool canUseToken)
        {
            // AuthKey is required but not checked, just pass in a dummy variable
            // to avoid having to track authkey, which is randomly cycled
            var url = new HttpUri(_settings.BaseUrl)
                .CombinePath("/torrents.php")
                .AddQueryParam("action", "download")
                .AddQueryParam("id", torrentId);

            return url.FullUri;
        }

        private string GetInfoUrl(string groupId, int torrentId)
        {
            var url = new HttpUri(_settings.BaseUrl)
                .CombinePath("/torrents.php")
                .AddQueryParam("id", groupId)
                .AddQueryParam("torrentid", torrentId);

            return url.FullUri;
        }
    }

    public class SecretCinemaSettingsValidator : AbstractValidator<SecretCinemaSettings>
    {
        public SecretCinemaSettingsValidator()
        {
            RuleFor(c => c.BaseUrl).IsValidUrl();
            RuleFor(c => c.Username).NotEmpty();
            RuleFor(c => c.Password).NotEmpty();
        }
    }

    public class SecretCinemaSettings : IIndexerSettings
    {
        private static readonly SecretCinemaSettingsValidator Validator = new SecretCinemaSettingsValidator();

        [FieldDefinition(1, Label = "Base Url", Type = FieldType.Select, SelectOptionsProviderAction = "getUrls", HelpText = "Select which baseurl Prowlarr will use for requests to the site")]
        public string BaseUrl { get; set; }

        [FieldDefinition(2, Label = "Username", HelpText = "Site Username", Privacy = PrivacyLevel.UserName)]
        public string Username { get; set; }

        [FieldDefinition(3, Label = "Password", HelpText = "Site Password", Privacy = PrivacyLevel.Password, Type = FieldType.Password)]
        public string Password { get; set; }

        [FieldDefinition(4)]
        public IndexerBaseSettings BaseSettings { get; set; } = new IndexerBaseSettings();

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
