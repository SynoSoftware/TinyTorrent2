using System.ComponentModel;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using MaxMind.Db;
using Syno.TinyTorrent.Library;
using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent.Services;

/// <summary>
/// Where a peer's address is: its country, from DB-IP's country data
/// installed with the app, and its city once the person downloads DB-IP's
/// city data. Both files are read in place; the city data is the replaceable
/// copy that <see cref="IDataset"/> describes, kept beside the Library
/// database. Lookups fail closed: an address the data lacks has no location.
/// </summary>
internal sealed class Geolocation(ProviderHttp http, BackgroundWork work) : IDataset, IDisposable
{
    private const string CityFileName = "dbip-city-lite.mmdb";
    private const string Name = "DB-IP";
    private static readonly string CountryPath = Path.Combine(
        AppContext.BaseDirectory,
        "Assets",
        "dbip-country-lite.mmdb"
    );
    private static readonly ProviderAccess Access = new(
        "dbip",
        RequestKey.Digest(string.Empty),
        "dataset"
    );
    private Reader? _countries = Open(CountryPath);
    private Reader? _cities;
    private string? _directory;
    private RequestPolicy? _policy;
    private DataDownload? _download;

    // Lookups share the files; Install and Delete replace the city file
    // alone, because Windows refuses to replace an open file.
    private readonly ReaderWriterLockSlim _file = new();

    /// <summary>Changes when the city data is installed, deleted or detached, so a kept location is looked up again.</summary>
    internal int Version { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>A failed download or deletion, which the window reports.</summary>
    internal event EventHandler<Exception>? Failed;

    public string Section => "city";

    public FileInfo? Installed =>
        _directory is { } directory
        && new FileInfo(Path.Combine(directory, CityFileName)) is { Exists: true } file
            ? file
            : null;

    /// <summary>DB-IP publishes its data once a month, so a copy from this month is the current one.</summary>
    public bool IsCurrent =>
        Installed is { LastWriteTimeUtc: var written }
        && written.Year == DateTime.UtcNow.Year
        && written.Month == DateTime.UtcNow.Month;

    public Task<long>? Count => null;

    public BackgroundWork.Operation? DataWork => _download?.Operation;

    public bool CanDownload => _policy is not null && _download is null;

    /// <summary>
    /// Serves the city data from <paramref name="directory"/>, downloading it
    /// through <paramref name="policy"/>, until <see cref="Detach"/>.
    /// </summary>
    internal void Attach(string directory, RequestPolicy policy)
    {
        _file.EnterWriteLock();
        try
        {
            _directory = directory;
            _policy = policy;
            _cities = Open(Path.Combine(directory, CityFileName));
            Version++;
        }
        finally
        {
            _file.ExitWriteLock();
        }
        Changed(string.Empty);
    }

    /// <summary>Ends a running download and closes the city data, and completes when both are done.</summary>
    internal async Task Detach()
    {
        await CancelDownload();
        _file.EnterWriteLock();
        try
        {
            _cities?.Dispose();
            _cities = null;
            _directory = null;
            _policy = null;
            Version++;
        }
        finally
        {
            _file.ExitWriteLock();
        }
        Changed(string.Empty);
    }

    /// <summary>The location of <paramref name="address"/>, or null when the data lacks it.</summary>
    internal Location? Find(IPAddress address)
    {
        _file.EnterReadLock();
        try
        {
            var country = _countries?.Find<Record>(address);
            var city = _cities?.Find<Record>(address);
            var place = country?.Country ?? city?.Country;
            return place?.Code is { } code
                ? new Location(code, place, city?.City, city?.Coordinates?.Point)
                : null;
        }
        // A damaged file gives no locations, as a missing one does.
        catch (InvalidDatabaseException)
        {
            return null;
        }
        finally
        {
            _file.ExitReadLock();
        }
    }

    public async Task<bool> Download()
    {
        if (!CanDownload || _directory is not { } directory || _policy is not { } policy)
            return false;
        var download = new DataDownload(work, new WorkDescription.City(CityPhase.Downloading));
        _download = download;
        ShowDownload();
        try
        {
            return await download.Run(
                () => Build(directory, policy, download),
                () =>
                {
                    download.Install(
                        new WorkDescription.City(CityPhase.Installing),
                        () => Install(directory)
                    );
                    return Task.CompletedTask;
                },
                () => Discard(directory),
                Report
            );
        }
        finally
        {
            _download = null;
            ShowDownload();
        }
    }

    /// <summary>Stops the running download at the person's request, and completes when it has ended.</summary>
    internal Task CancelDownload() => StopDownload(interrupted: false);

    /// <summary>
    /// Stops the running download as one the person did not ask for, which
    /// reports it, and completes when the download has ended.
    /// </summary>
    internal Task InterruptDownload() => StopDownload(interrupted: true);

    public async Task Delete()
    {
        if (_download is not null || _directory is not { } directory)
            return;
        using var operation = work.Begin(
            new(
                new WorkDescription.Maintenance(MaintenanceWork.DeleteDataset),
                new WorkProgress.Unknown(),
                new WorkExecution.Finishing()
            )
        );
        try
        {
            await Task.Run(() => Replace(() => File.Delete(Path.Combine(directory, CityFileName))));
            operation.Outcome = WorkOutcome.Completed;
        }
        catch (Exception error)
        {
            operation.Error = error;
            Report(error);
        }
        Changed(nameof(Installed));
    }

    public void Dispose()
    {
        _file.EnterWriteLock();
        try
        {
            _cities?.Dispose();
            _cities = null;
            _countries?.Dispose();
            _countries = null;
        }
        finally
        {
            _file.ExitWriteLock();
        }
    }

    private Task StopDownload(bool interrupted)
    {
        if (_download is not { } download)
            return Task.CompletedTask;
        var stopped = download.Stop(interrupted);
        ShowDownload();
        return stopped;
    }

    // Downloads the month's archive and unpacks it beside the installed file,
    // which stays in use until Install.
    private async Task Build(string directory, RequestPolicy policy, DataDownload download)
    {
        var building = Building(directory);
        var archive = Path.Combine(directory, CityFileName + ".gz");
        var fresh = FreshRequest.Once(Access);
        var cancellation = download.Cancellation;
        try
        {
            await Fetch(
                archive,
                policy,
                fresh,
                progress =>
                    download.Report(new WorkDescription.City(CityPhase.Downloading), progress),
                cancellation
            );
            await Task.Run(
                () =>
                    Unpack(
                        archive,
                        building,
                        percent =>
                            download.Report(
                                new WorkDescription.City(CityPhase.Unpacking),
                                new WorkProgress.Fraction(percent, 100, WorkUnit.Percent, true)
                            ),
                        cancellation
                    ),
                cancellation
            );
        }
        catch (HttpRouteException error)
        {
            throw new RouteException(error);
        }
        catch (Exception error) when (error is InvalidDataException or InvalidDatabaseException)
        {
            throw new VideoException(VideoFailure.DownloadDamaged, error);
        }
        catch (IOException error) when (DataDownload.IsDiskFull(error))
        {
            throw new VideoException(VideoFailure.DiskFull, error);
        }
        catch (Exception error)
            when (error is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            throw new VideoException(VideoFailure.DownloadFailed, error);
        }
        finally
        {
            File.Delete(archive);
        }
    }

    // Downloads beside the archive and renames the file when it is complete.
    // DB-IP publishes a month's file during its first hours, so the previous
    // month's file stands in until then.
    private async Task Fetch(
        string archive,
        RequestPolicy policy,
        FreshRequest fresh,
        Action<WorkProgress> progress,
        CancellationToken cancellation
    )
    {
        var part = archive + ".part";
        var now = DateTime.UtcNow;
        try
        {
            try
            {
                await Get(now).ConfigureAwait(false);
            }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
            {
                await Get(now.AddMonths(-1)).ConfigureAwait(false);
            }
            File.Move(part, archive, overwrite: true);
        }
        finally
        {
            File.Delete(part);
        }

        async Task Get(DateTime month)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri($"https://download.db-ip.com/free/dbip-city-lite-{month:yyyy-MM}.mmdb.gz")
            );
            var call = new ProviderCall(request, policy, new(Access)) { Fresh = fresh };
            await http.Download(call, part, progress, cancellation).ConfigureAwait(false);
        }
    }

    // Writes the file that Install moves into place, and checks that it is a
    // readable database before the installed copy is replaced.
    private static void Unpack(
        string archive,
        string building,
        Action<int> report,
        CancellationToken cancellation
    )
    {
        using (var packed = File.OpenRead(archive))
        using (var content = new GZipStream(packed, CompressionMode.Decompress))
        using (var unpacked = new FileStream(building, FileMode.Create, FileAccess.ReadWrite))
        {
            var buffer = new byte[1 << 16];
            int count;
            while ((count = content.Read(buffer)) > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                unpacked.Write(buffer, 0, count);
                report((int)Math.Min(99, packed.Position * 100 / packed.Length));
            }
            // A power cut after the move must not leave a file whose data
            // never reached the disk.
            unpacked.Flush(flushToDisk: true);
        }
        using var reader = new Reader(building);
    }

    private void Install(string directory)
    {
        Replace(() =>
            File.Move(Building(directory), Path.Combine(directory, CityFileName), overwrite: true)
        );
        Changed(nameof(Installed));
    }

    // Closes the city data around a change of its file, and serves the file
    // that remains afterwards.
    private void Replace(Action change)
    {
        _file.EnterWriteLock();
        try
        {
            _cities?.Dispose();
            _cities = null;
            change();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new VideoException(VideoFailure.InUse, error);
        }
        finally
        {
            if (_directory is { } directory)
                _cities = Open(Path.Combine(directory, CityFileName));
            Version++;
            _file.ExitWriteLock();
        }
    }

    // A build that another program holds is deleted by the next build.
    private static void Discard(string directory)
    {
        try
        {
            File.Delete(Building(directory));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static string Building(string directory) =>
        Path.Combine(directory, CityFileName + ".tmp");

    // A missing or damaged file gives no locations; a damaged city file is
    // replaced by the next download.
    private static Reader? Open(string path)
    {
        try
        {
            return File.Exists(path) ? new Reader(path) : null;
        }
        catch (Exception error)
            when (error is IOException or UnauthorizedAccessException or InvalidDatabaseException)
        {
            return null;
        }
    }

    private void Report(Exception error)
    {
        if (error is VideoException failure)
            failure.Subject ??= Name;
        Failed?.Invoke(this, error);
    }

    private void ShowDownload()
    {
        Changed(nameof(DataWork));
        Changed(nameof(CanDownload));
    }

    private void Changed(string property) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    // The parts of a DB-IP record that the app reads; the reader fills the
    // constructor from the record's keys and leaves absent keys null.
    internal sealed class Record
    {
        [Constructor]
        public Record(
            [MapKey("country")] Place? country,
            [MapKey("city")] Place? city,
            [MapKey("location")] Coordinates? coordinates
        )
        {
            Country = country;
            City = city;
            Coordinates = coordinates;
        }

        internal Place? Country { get; }
        internal Place? City { get; }
        internal Coordinates? Coordinates { get; }
    }

    internal sealed class Coordinates
    {
        [Constructor]
        public Coordinates(
            [MapKey("latitude")] double? latitude,
            [MapKey("longitude")] double? longitude
        )
        {
            if (latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180)
                Point = new GeoPoint(longitude.Value, latitude.Value);
        }

        internal GeoPoint? Point { get; }
    }
}

/// <summary>A country or city with its names in the languages DB-IP ships.</summary>
internal sealed class Place
{
    private readonly IReadOnlyDictionary<string, string>? _names;

    [Constructor]
    public Place(
        [MapKey("iso_code")] string? code,
        [MapKey("names")] IReadOnlyDictionary<string, string>? names
    )
    {
        Code = code;
        _names = names;
    }

    /// <summary>The ISO 3166-1 alpha-2 code of a country; a city has none.</summary>
    internal string? Code { get; }

    /// <summary>The name in <paramref name="language"/>, in English when that language is missing, or null without names.</summary>
    internal string? Name(string language) =>
        _names is not null
        && (_names.TryGetValue(language, out var name) || _names.TryGetValue("en", out name))
            ? name
            : null;
}

/// <summary>Where an address is: its country, whose <paramref name="Code"/> also names the flag, and with city data its city.</summary>
internal sealed record Location(string Code, Place Country, Place? City, GeoPoint? Point)
{
    /// <summary>The country's name in <paramref name="language"/>, or its code when the data names it in no language.</summary>
    internal string CountryName(string language) => Country.Name(language) ?? Code;
}

/// <summary>
/// A place the peer map groups peers into. With Countries detail it is a whole
/// country; with Cities detail it is one coordinate, or the country's peers
/// that have none.
/// </summary>
internal readonly record struct PeerPlace(string Code, GeoPoint? Point, bool Cities)
{
    internal static PeerPlace Of(Location location, bool cities) =>
        new(location.Code, cities ? location.Point : null, cities);

    internal bool Contains(Location? location) =>
        location is not null && Of(location, Cities) == this;
}
