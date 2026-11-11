param(
    [string] $WindowPath,
    [Parameter(Mandatory)][string] $Directory,
    [string] $PresentMonPath,
    [switch] $Analyze,
    [switch] $ElevatedRecorder,
    [switch] $InputOnly,
    [switch] $Replay,
    [switch] $Hold
)

$ErrorActionPreference = 'Stop'
if ($Replay -and (-not $InputOnly -or $ElevatedRecorder)) {
    throw '-Replay is an offscreen command diagnostic and requires -InputOnly without elevation.'
}
$culture = [Globalization.CultureInfo]::InvariantCulture
function Number($value) { [double]::Parse([string]$value, $culture) }
function ReadReview([switch] $AllowFailure) {
    $review = Get-Content -LiteralPath (Join-Path $Directory 'review.json') -Raw | ConvertFrom-Json
    if ($review.failure -and -not $AllowFailure) { throw $review.failure }
    $review
}
function Summary($values) {
    $ordered = @($values | Sort-Object)
    if (-not $ordered.Count) { return $null }
    [ordered]@{ count = $ordered.Count; median = $ordered[[math]::Floor(($ordered.Count - 1) / 2)];
        p95 = $ordered[[math]::Ceiling($ordered.Count * .95) - 1]; worst = $ordered[-1] }
}
function MemorySummary($samples) {
    if (-not $samples.Count) { return $null }
    [ordered]@{ samples = $samples.Count;
        startPrivateMiB = $samples[0].privateBytes / 1MB;
        peakPrivateMiB = ($samples.privateBytes | Measure-Object -Maximum).Maximum / 1MB;
        endPrivateMiB = $samples[-1].privateBytes / 1MB;
        privateGrowthMiB = ($samples[-1].privateBytes - $samples[0].privateBytes) / 1MB;
        startWorkingSetMiB = $samples[0].workingSetBytes / 1MB;
        peakWorkingSetMiB = ($samples.workingSetBytes | Measure-Object -Maximum).Maximum / 1MB;
        endWorkingSetMiB = $samples[-1].workingSetBytes / 1MB;
        processPeakWorkingSetMiB = ($samples.peakWorkingSetBytes | Measure-Object -Maximum).Maximum / 1MB }
}
function Pacing($stamps, [double] $start, [double] $end, [double] $frequency, [double] $period) {
    $ticks = @([double]$start) + @($stamps | Sort-Object -Unique) + @([double]$end)
    $gaps = @(for ($index = 1; $index -lt $ticks.Count; $index++) {
        ($ticks[$index] - $ticks[$index - 1]) * 1000 / $frequency
    })
    $missed = 0
    foreach ($gap in $gaps) { $missed += [math]::Max(0, [math]::Round($gap / $period) - 1) }
    $slowest = @($gaps | Sort-Object -Descending | Select-Object -First ([math]::Max(1, [math]::Ceiling($gaps.Count / 100))))
    [ordered]@{ fps = @($stamps).Count * $frequency / ($end - $start);
        low1Percent = 1000 / (($slowest | Measure-Object -Average).Average);
        frameMs = Summary $gaps; missedRefreshes = $missed;
        longHolds = @($gaps | Where-Object { $_ -gt 1.5 * $period }).Count;
        stalls = @($gaps | Where-Object { $_ -gt 100 }).Count }
}
function DotSummary($frames, $phase, [double] $frequency, [double] $period) {
    $frames = @($frames | Where-Object { $null -ne $_.dots -and $_.qpc -ge $phase.started -and $_.qpc -lt $phase.ended })
    if (-not $frames.Count) { return $null }
    $visible = @($frames | Where-Object { $_.dots.count -gt 0 })
    $advancing = 0; $frozen = 0; $previous = $null; $previousKey = $null
    foreach ($frame in $frames) {
        $dot = $frame.dots
        $key = if ($dot.count -gt 0) { '{0}|{1}|{2}' -f $dot.start, $dot.upload, ($dot.route | ConvertTo-Json -Compress) } else { $null }
        if ($null -ne $key -and $key -eq $previousKey) {
            if ($dot.progress -gt $previous.progress) { $advancing++ } else { $frozen++ }
        }
        $previous = $dot; $previousKey = $key
    }
    [ordered]@{ sampledDraws = $frames.Count; visibleDraws = $visible.Count; drawsWithoutDots = $frames.Count - $visible.Count;
        visiblePercent = 100 * $visible.Count / $frames.Count;
        maximumVisibleDots = ($frames | ForEach-Object { $_.dots.count } | Measure-Object -Maximum).Maximum;
        advancingSameDotPairs = $advancing; frozenSameDotPairs = $frozen;
        visibleDrawPacing = Pacing @($visible.qpc) $phase.started $phase.ended $frequency $period }
}
function Report {
    $ready = Get-Content -LiteralPath (Join-Path $Directory 'ready.json') -Raw | ConvertFrom-Json
    if ($ready.replay -and -not $InputOnly) { throw 'Offscreen replay has no displayed-frame evidence; analyze it with -InputOnly.' }
    $timing = Get-Content -LiteralPath (Join-Path $Directory 'timing.json') -Raw | ConvertFrom-Json
    $review = ReadReview -AllowFailure
    $frequency = [double]$ready.frequency
    if ($ready.refreshHz -le 1) { throw 'The monitor refresh rate could not be determined; missed-refresh counts would be unreliable.' }
    $period = 1000 / $ready.refreshHz
    $memory = @()
    $memoryPath = Join-Path $Directory 'memory.json'
    if (Test-Path -LiteralPath $memoryPath) { $memory = @(Get-Content -LiteralPath $memoryPath -Raw | ConvertFrom-Json) }
    $probes = @()
    $probePath = Join-Path $Directory 'ui-probes.json'
    if (Test-Path -LiteralPath $probePath) { $probes = @(Get-Content -LiteralPath $probePath -Raw | ConvertFrom-Json) }
    $source = @()
    $sourcePath = Join-Path $Directory 'source-updates.json'
    if (Test-Path -LiteralPath $sourcePath) { $source = @(Get-Content -LiteralPath $sourcePath -Raw | ConvertFrom-Json) }
    $raw = @()
    if (-not $InputOnly) { $raw = @(Import-Csv -LiteralPath (Join-Path $Directory 'frames.csv')) }
    if (-not $InputOnly -and (-not $raw.Count -or -not $raw[0].PSObject.Properties['CPUStartQPC'])) {
        throw 'No PresentMon QPC records were captured. See recorder.log and recorder-error.log.'
    }
    $displayed = @($raw | Where-Object { $_.SwapChainAddress -ne '0x0' -and $_.DisplayedTime -ne 'NA' -and (Number $_.DisplayedTime) -gt 0 } | ForEach-Object {
        [pscustomobject]@{ chain = $_.SwapChainAddress;
            qpc = (Number $_.CPUStartQPC) + (Number $_.DisplayLatency) * $frequency / 1000;
            click = if ($_.ClickToPhotonLatency -and $_.ClickToPhotonLatency -ne 'NA') { Number $_.ClickToPhotonLatency } else { 0 } }
    } | Sort-Object qpc)
    if (-not $InputOnly -and -not $displayed.Count) { throw 'The recorder saw no displayed frames; a smoothness verdict is unavailable.' }
    $phases = @(foreach ($phase in $timing.phases) {
        $surfaces = @($displayed | Group-Object chain | Where-Object {
            $_.Group[0].qpc -lt $phase.ended -and $_.Group[-1].qpc -ge $phase.started
        } | ForEach-Object {
            $chain = $_.Name
            $within = @($_.Group | Where-Object { $_.qpc -ge $phase.started -and $_.qpc -lt $phase.ended })
            [pscustomobject]@{ chain = $chain;
                pacing = Pacing @($within.qpc) $phase.started $phase.ended $frequency $period;
                droppedPresents = @($raw | Where-Object { $_.SwapChainAddress -eq $chain -and
                    $_.DisplayedTime -eq 'NA' -and (Number $_.CPUStartQPC) -ge $phase.started -and
                    (Number $_.CPUStartQPC) -lt $phase.ended }).Count;
                osClickMs = Summary @($within.click | Where-Object { $_ -gt 0 }) }
        })
        $unique = @($timing.mapFrames | Where-Object { $_.phase -eq $phase.name } |
            Sort-Object qpc | Select-Object -Property x, y, scale, qpc)
        $updates = [Collections.Generic.List[double]]::new()
        $previous = $null
        foreach ($picture in $unique) {
            if ($null -eq $previous -or $picture.x -ne $previous.x -or $picture.y -ne $previous.y -or $picture.scale -ne $previous.scale) {
                if ($picture.qpc -ge $phase.started -and $picture.qpc -lt $phase.ended) { $updates.Add([double]$picture.qpc) }
                $previous = $picture
            }
        }
        [pscustomobject]@{ name = $phase.name; durationMs = ($phase.ended - $phase.started) * 1000 / $frequency;
            failed = [bool]$phase.failed;
            uiResponseMs = Summary @($probes | Where-Object { $_.Sent -ge $phase.started -and $_.Sent -lt $phase.ended } |
                ForEach-Object { ($_.Completed - $_.Sent) * 1000 / $frequency });
            uiTimeouts = @($probes | Where-Object { $_.Sent -ge $phase.started -and $_.Sent -lt $phase.ended -and -not $_.Answered }).Count;
            dots = DotSummary @($timing.mapFrames | Where-Object phase -eq $phase.name) $phase $frequency $period;
            drawnArcs = Summary @($timing.mapFrames | Where-Object { $_.phase -eq $phase.name -and $null -ne $_.arcs } | ForEach-Object arcs);
            routesInView = Summary @($timing.mapFrames | Where-Object { $_.phase -eq $phase.name -and $null -ne $_.visibleRoutes } | ForEach-Object visibleRoutes);
            locationMarkers = Summary @($timing.mapFrames | Where-Object { $_.phase -eq $phase.name -and $null -ne $_.locations } | ForEach-Object locations);
            visibleMarkers = Summary @($timing.mapFrames | Where-Object { $_.phase -eq $phase.name -and $null -ne $_.visibleLocations } | ForEach-Object visibleLocations);
            receivedInput = [ordered]@{
                pans = @($timing.received | Where-Object { $_.phase -eq $phase.name -and $_.kind -eq 'pan' }).Count;
                wheels = @($timing.received | Where-Object { $_.phase -eq $phase.name -and $_.kind -eq 'wheel' }).Count;
                wheelDelta = ($timing.received | Where-Object { $_.phase -eq $phase.name -and $_.kind -eq 'wheel' } | Measure-Object delta -Sum).Sum };
            memory = MemorySummary @($memory | Where-Object { $_.qpc -ge $phase.started -and $_.qpc -lt $phase.ended });
            inputLatenessMs = $phase.WorstLatenessMs; surfaces = $surfaces;
            geography = if ($phase.name -match 'pan|zoom') { Pacing $updates.ToArray() $phase.started $phase.ended $frequency $period } else { $null };
            animation = if ($phase.name -match 'animation') {
                Pacing @($timing.mapFrames | Where-Object { $_.phase -eq $phase.name -and $_.qpc -ge $phase.started -and $_.qpc -lt $phase.ended } | ForEach-Object qpc) $phase.started $phase.ended $frequency $period
            } else { $null };
            firstRenderMs = if ($phase.firstPicture) { ($phase.firstPicture - $phase.started) * 1000 / $frequency } else { $null };
            firstRenderEndpoint = if ($phase.firstPictureEndpoint) { $phase.firstPictureEndpoint } else { 'xaml-completion' } }
    })
    $clicks = @(foreach ($click in $timing.clicks) {
        [pscustomobject]@{ torrentId = $click.TorrentId; visit = $click.Visit; sent = $click.sent;
            alreadySelected = [bool]$click.alreadySelected; matches = $click.matches;
            receivedMs = if ($click.received) { ($click.received - $click.sent) * 1000 / $frequency } else { $null };
            selectedMs = if ($click.selected) { ($click.selected - $click.sent) * 1000 / $frequency } else { $null };
            renderMs = if ($click.rendered) { ($click.rendered - $click.sent) * 1000 / $frequency } else { $null };
            renderMegacycles = if ($click.renderCycles) { ([double]$click.renderCycles - $click.pressCycles) / 1e6 } else { $null };
            pressedOn = $click.pressedOn;
            # A committed frame is taken as on screen two refreshes later, after the compositor's next frame.
            sentBeforeViewChange = if ($null -ne $click.viewShown -and $click.matches -eq 1) {
                $click.viewShown + 2 * $period * $frequency / 1000 -gt $click.sent } else { $null };
            afterViewChangeMs = if ($click.viewShown -and $click.matches -eq 1) { ($click.sent - $click.viewShown) * 1000 / $frequency } else { $null };
            lostCause = if ($click.selected) { $null }
                elseif ($click.matches -ne 1) { 'press not matched' }
                elseif ($click.pressedOn -eq 'no row') { 'press found no row' }
                elseif ($click.pressedOn -eq 'leaving row') { 'press on a leaving row' }
                elseif (-not $click.pressGesture) { 'no gesture record' }
                elseif ($click.pressGesture.Gesture -eq 'None') { 'press armed no gesture' }
                elseif (-not $click.pressGesture.SameItem) { 'press resolved another row' }
                elseif (-not $click.pressGesture.Deferred) { 'press applied, selected nothing' }
                elseif (-not $click.releaseGesture) { 'no release observed' }
                elseif ($click.releaseGesture.Gesture -eq 'None') { 'gesture cancelled before release' }
                else { 'release dispatched nothing' } }
    })
    $cadence = @(for ($index = 1; $index -lt $clicks.Count; $index++) { ($clicks[$index].sent - $clicks[$index - 1].sent) * 1000 / $frequency })
    $marqueePhase = $timing.phases | Where-Object name -eq 'torrent-marquee'
    $marquee = @($timing.samples | Where-Object { $_.phase -eq 'torrent-marquee' -and $_.marquee -and
        $_.qpc -ge $marqueePhase.started -and $_.qpc -lt $marqueePhase.ended })
    if ($marqueePhase -and -not $marqueePhase.failed -and $marquee.Count -lt 2) {
        throw 'The gesture did not yield enough marquee frames for a smoothness measurement.'
    }
    $moves = @(for ($index = 1; $index -lt $marquee.Count; $index++) {
        $a = $marquee[$index - 1]; $b = $marquee[$index]
        $dx = $b.marquee.x - $a.marquee.x
        $dy = ($b.marquee.y + $b.marquee.height) - ($a.marquee.y + $a.marquee.height)
        [pscustomobject]@{ ms = ($b.qpc - $a.qpc) * 1000 / $frequency; dips = [math]::Sqrt($dx * $dx + $dy * $dy) }
    })
    [object[]] $motion = if ($marquee.Count) { @($marquee[0]) } else { @() }
    for ($index = 1; $index -lt $marquee.Count; $index++) { if ($moves[$index - 1].dips -gt .01) { $motion += $marquee[$index] } }
    $turnover = @(foreach ($group in ($timing.tables | Group-Object table, subject, phase)) {
        $previous = $null
        foreach ($sample in $group.Group) {
            $keys = @($sample.rows.key)
            if ($previous) {
                $leaving = @($previous.rows.key | Where-Object { $_ -notin $keys }).Count
                $moved = @(for ($index = 0; $index -lt $keys.Count; $index++) {
                    $before = [array]::IndexOf(@($previous.rows.key), $keys[$index])
                    if ($before -ge 0 -and $before -ne $index) { $keys[$index] }
                }).Count
                if ($leaving -gt 0 -or $moved -gt 0) {
                    [pscustomobject]@{ phase = $sample.phase; table = $sample.table; subject = $sample.subject;
                        qpc = $sample.qpc; visible = $keys.Count; leaving = $leaving; moved = $moved;
                        sampleGapMs = ($sample.qpc - $previous.qpc) * 1000 / $frequency }
                }
            }
            $previous = $sample
        }
    })
    $report = [ordered]@{ display = $ready; displayTrace = -not $InputOnly; replay = [bool]$ready.replay;
        variant = if ($timing.scopeReview) { 'peer-scope' } else { 'standard' };
        failure = $review.failure; fixture = $timing.fixture;
        frameBudgetMs = $period; phases = $phases; clicks = $clicks;
        source = $source; tableTurnover = $turnover;
        sourceGenerationMs = Summary @($source.generationMs);
        sourceCadenceMs = Summary @(for ($index = 2; $index -lt $source.Count; $index++) {
            ($source[$index].produced - $source[$index - 1].produced) * 1000 / $frequency
        });
        sourceDeliveryMs = Summary @($timing.updates | ForEach-Object { ($_.received - $_.Produced) * 1000 / $frequency });
        commandDeliveryMs = Summary @($timing.dispatched | ForEach-Object { ($_.received - $_.sent) * 1000 / $frequency });
        memory = MemorySummary $memory;
        memoryPasses = @(foreach ($visit in ($clicks | Group-Object visit)) {
            $start = $visit.Group[0].sent; $end = $visit.Group[-1].sent + .15 * $frequency
            [pscustomobject]@{ visit = $visit.Name; memory = MemorySummary @($memory | Where-Object { $_.qpc -ge $start -and $_.qpc -lt $end }) }
        });
        selectionMs = Summary @($clicks | Where-Object { -not $_.alreadySelected -and $null -ne $_.selectedMs } | ForEach-Object selectedMs);
        renderMs = Summary @($clicks | Where-Object { -not $_.alreadySelected -and $null -ne $_.renderMs } | ForEach-Object renderMs);
        renderMegacycles = Summary @($clicks | Where-Object { -not $_.alreadySelected -and $null -ne $_.renderMegacycles } | ForEach-Object renderMegacycles);
        feedbackMs = Summary @($clicks.renderMs | Where-Object { $null -ne $_ });
        alreadySelected = @($clicks | Where-Object alreadySelected).Count;
        invalidClickMatches = @($clicks | Where-Object { $null -ne $_.matches -and $_.matches -ne 1 }).Count;
        unselected = @($clicks | Where-Object { $null -eq $_.selectedMs }).Count;
        lostCauses = @($clicks | Where-Object lostCause | Group-Object lostCause | ForEach-Object {
            [pscustomobject]@{ cause = $_.Name; count = $_.Count } });
        notRendered = @($clicks | Where-Object { $null -eq $_.renderMs }).Count;
        sentBeforeViewChange = @($clicks | Where-Object sentBeforeViewChange | Group-Object pressedOn | ForEach-Object {
            [pscustomobject]@{ pressedOn = $_.Name; count = $_.Count } });
        clickCadenceMs = Summary $cadence;
        cadenceValid = if ($cadence.Count) { @($cadence | Where-Object { $_ -lt 135 -or $_ -gt 165 }).Count -eq 0 } else { $null };
        marqueeSamples = $marquee.Count;
        marquee = if ($marquee.Count -ge 2) { Pacing @($motion.qpc) $marqueePhase.started $marqueePhase.ended $frequency $period } else { $null };
        marqueeMovement = $moves; work = @($timing.work | Group-Object phase, name | ForEach-Object {
            [pscustomobject]@{ name = $_.Name; milliseconds = Summary $_.Group.ms } });
        outcomes = $review.outcomes;
        limits = @('Displayed surfaces are measured separately, never summed.',
            'Navigation updates are changed views sampled after the map draw handler; they distinguish navigation stalls from animated dots.',
            'Map startup phases identify their timestamp endpoint: XAML completion or a UI poll observing the requested drawn map. Neither establishes displayed pixels; an idle XAML surface need not render when the independent map draws.',
            'Selection latency ends at the real selection event; render latency ends at XAML completion, before scanout.',
            $(if ($timing.scopeReview) {
                'Peer-scope attribution selects 14, then 15, then 14 torrents programmatically and waits for each inventory and table view. Common Peer identity and notification counts are recorded; no live rate/churn, timed clicks or navigation schedule runs.'
            } elseif ($ready.replay) {
                'Offscreen replay schedules application commands independently at 120 Hz. Selection uses the current first seven row slots. Commands bypass native delivery, hit-testing, gesture arbitration and pointer focus. Exact send timestamps correlate selection acknowledgements; queued commands are not skipped.'
            } else {
                'Clicks target seven physical row slots while sorting continues. Input timestamps are matched against calibrated sender uptime within 50 ms; raw QPC send times measure latency. Already-selected clicks are acknowledgements, excluded from switch-latency summaries.'
            }),
            'The external WM_NULL round-trip probe measures window-thread availability, including scheduling delay. It is sampled about every 16 ms, times out after 1 second, and does not establish pixel latency.',
            'Visible table membership is sampled at XAML completion about every 100 ms. Turnover counts and gaps describe observed rows; intermediate sorts during a stall may be coalesced.',
            'OS input-to-display latency concerns an affected frame, not identified highlight pixels.',
            'Marquee movement is sampled at XAML render completion, not pixel capture.',
            'Dot visibility counts sprites submitted after clipping and fading. Progress comparisons follow the same dot across consecutive draws; no visible dots can also mean traffic is outside the zoomed viewport. These are render observations, not displayed-pixel proof.',
            'Memory samples cover the UI process, including its managed and native allocations and resident synthetic fixture. Working set is resident RAM; private bytes are private committed memory. GPU VRAM and engine/driver-process memory are excluded.',
            'Memory is sampled externally at approximately 100 ms intervals during the diagnostic journey. Private-byte peaks are sampled; the process working-set peak is also recorded. Growth across passes is evidence to investigate, not proof of a leak.',
            'Renderer operation durations are elapsed time, including any waits; their journey labels are sampled when the UI dispatcher delivers the measurement.',
            'Missed refreshes are estimated from frame holds at the recorded monitor rate. First and last holds are clipped to the journey.',
            'A frame gap over 1.5 refresh intervals counts as uneven pacing. No average FPS alone earns a smooth verdict.') }
    $report | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath (Join-Path $Directory 'performance.json') -Encoding utf8NoBOM
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('# TinyTorrent UI frame pacing')
    $lines.Add('')
    $lines.Add(('1920×1080 at {0} Hz; frame budget {1:F2} ms; desktop scale {2}.' -f $ready.refreshHz, $period, $ready.scale))
    $lines.Add(('{0} synthetic torrents; {1} peer rows.' -f @($ready.torrents).Count, ($ready.torrents.rows | Measure-Object -Sum).Sum))
    if ($ready.detail) { $lines.Add(('Map detail: {0}.' -f $ready.detail)) }
    if ($ready.arcLimit) { $lines.Add(('{0} fixed locations; arc limit {1}.' -f $ready.locations, $ready.arcLimit)) }
    if ($timing.scopeReview) {
        $lines.Add('Variant: peer-scope attribution. Selects 14 → 15 → 14 torrents and waits for each scope. Records common Peer identity and aggregate notifications; runs no live rate/churn, timed clicks or navigation schedule.')
        $lines.Add(('{0} fixed connection slots.' -f $ready.connections))
    } elseif ($ready.connections) { $lines.Add(('{0} connection slots; {1} disconnect/connect swaps per second in the churn phase.' -f $ready.connections, $ready.swapsPerSecond)) }
    if ($InputOnly) { $lines.Add('Render-only run: no display trace was recorded. Draw and XAML completion rates do not establish displayed FPS.') }
    if ($ready.replay -and -not $timing.scopeReview) {
        $lines.Add('Offscreen command replay: selection timings start at external command emission. Native input, hit-testing, foreground pacing and physical display latency are unverified. This procedure requires its own matched baseline.')
        $lines.Add(('Command emission → UI application: p95 {0:F1} ms, worst {1:F1} ms.' -f $report.commandDeliveryMs.p95, $report.commandDeliveryMs.worst))
    }
    if ($review.failure) { $lines.Add(('FAILED RUN: completed phases and the failed interval are reported below. {0}' -f $review.failure)) }
    $lines.Add('')
    $lines.Add('| Journey | Display surface | FPS | 1% low | p95 ms | Worst ms | Missed refreshes | Dropped presents | Holds >1.5 refreshes |')
    $lines.Add('|---|---|---:|---:|---:|---:|---:|---:|---:|')
    foreach ($phase in $phases) {
        if ($phase.failed) { $lines.Add(('| {0} | FAILED interval ({1:F1} ms) | | | | | | | |' -f $phase.name, $phase.durationMs)) }
        if ($phase.locationMarkers) { $lines.Add(('| {0} | Location markers: {1} prepared; {2} visible (maximum), independently of arc cap | | | | | | | |' -f
            $phase.name, $phase.locationMarkers.worst, $phase.visibleMarkers.worst)) }
        if ($phase.uiResponseMs) { $lines.Add(('| {0} | UI thread response: p95 {1:F1} ms, worst {2:F1} ms, timeouts {3} | | | | | | | |' -f
            $phase.name, $phase.uiResponseMs.p95, $phase.uiResponseMs.worst, $phase.uiTimeouts)) }
        if ($phase.dots) { $lines.Add(('| {0} | Dots visible in {1:F1}% of draws; {2} draws without dots; advancing/frozen same-dot pairs {3}/{4}; worst visible-dot draw gap {5:F1} ms | | | | | | | |' -f
            $phase.name, $phase.dots.visiblePercent, $phase.dots.drawsWithoutDots, $phase.dots.advancingSameDotPairs,
            $phase.dots.frozenSameDotPairs, $phase.dots.visibleDrawPacing.frameMs.worst)) }
        if ($phase.drawnArcs) { $lines.Add(('| {0} | Retained active arcs: median {1}, maximum {2} | | | | | | | |' -f
            $phase.name, $phase.drawnArcs.median, $phase.drawnArcs.worst)) }
        if ($phase.routesInView) { $lines.Add(('| {0} | Route bounds intersecting view: median {1}, maximum {2} | | | | | | | |' -f
            $phase.name, $phase.routesInView.median, $phase.routesInView.worst)) }
        if (-not $ready.replay -and $phase.name -match 'zoom') { $lines.Add(('| {0} | Processed wheel events: {1}; total delta: {2} | | | | | | | |' -f
            $phase.name, $phase.receivedInput.wheels, $phase.receivedInput.wheelDelta)) }
        foreach ($surface in $phase.surfaces) {
            $p = $surface.pacing
            $lines.Add(('| {0} | {1} | {2:F1} | {3:F1} | {4:F1} | {5:F1} | {6} | {7} | {8} |' -f $phase.name, $surface.chain,
                $p.fps, $p.low1Percent, $p.frameMs.p95, $p.frameMs.worst, $p.missedRefreshes, $surface.droppedPresents, $p.longHolds))
            if ($surface.osClickMs) { $lines.Add(('| {0} | OS click → display: p95 {1:F1} ms, worst {2:F1} ms | | | | | | | |' -f
                $phase.name, $surface.osClickMs.p95, $surface.osClickMs.worst)) }
        }
        if ($phase.geography) { $p = $phase.geography; $lines.Add(('| {0} | Navigation updates (drawn) | {1:F1} | {2:F1} | {3:F1} | {4:F1} | {5} | — | {6} |' -f
            $phase.name, $p.fps, $p.low1Percent, $p.frameMs.p95, $p.frameMs.worst, $p.missedRefreshes, $p.longHolds)) }
        if ($phase.animation) { $p = $phase.animation; $lines.Add(('| {0} | Map draws (before display) | {1:F1} | {2:F1} | {3:F1} | {4:F1} | {5} | — | {6} |' -f
            $phase.name, $p.fps, $p.low1Percent, $p.frameMs.p95, $p.frameMs.worst, $p.missedRefreshes, $p.longHolds)) }
        if ($null -ne $phase.firstRenderMs) { $lines.Add(('| {0} | First map observation ({1}): {2:F1} ms; scenario ready: {3:F1} ms | | | | | | | |' -f $phase.name, $phase.firstRenderEndpoint, $phase.firstRenderMs, $phase.durationMs)) }
    }
    $lines.Add('')
    if ($source.Count) {
        $lines.Add(('External source generation: p95 {0:F1} ms, worst {1:F1} ms. Source → UI receipt: p95 {2:F1} ms, worst {3:F1} ms.' -f
            $report.sourceGenerationMs.p95, $report.sourceGenerationMs.worst, $report.sourceDeliveryMs.p95, $report.sourceDeliveryMs.worst))
        $lines.Add(('Source update cadence: median {0:F1} ms, worst {1:F1} ms.' -f $report.sourceCadenceMs.median, $report.sourceCadenceMs.worst))
        $last = $source[-1]
        $lines.Add(('Final source state: {0} established, {1} connecting, {2} handshaking; {3} established peers scheduled to disconnect.' -f
            $last.connected, $last.connecting, $last.handshaking, $last.pendingDisconnect))
    }
    foreach ($group in ($turnover | Group-Object phase, table)) {
        $leaving = Summary @($group.Group.leaving)
        $moved = Summary @($group.Group.moved)
        $lines.Add(('Observed row shuffle ({0}): {1} changes; median/worst rows leaving {2}/{3}; retained rows changing rank {4}/{5}.' -f
            $group.Name, $leaving.count, $leaving.median, $leaving.worst, $moved.median, $moved.worst))
    }
    $lines.Add('')
    if ($clicks.Count) {
        $lines.Add(('Already-selected clicks: {0}. Missing or ambiguous input matches: {1}.' -f $report.alreadySelected, $report.invalidClickMatches))
        $lines.Add(('Click → selection: median {0:F1} ms, p95 {1:F1} ms, worst {2:F1} ms.' -f $report.selectionMs.median, $report.selectionMs.p95, $report.selectionMs.worst))
        $lines.Add(('Click → rendered selection: median {0:F1} ms, p95 {1:F1} ms, worst {2:F1} ms. Unselected: {3}; not observed rendered: {4}.' -f
            $report.renderMs.median, $report.renderMs.p95, $report.renderMs.worst, $report.unselected, $report.notRendered))
        if ($report.lostCauses.Count) {
            $lines.Add(('Unselected clicks by cause: {0}.' -f (($report.lostCauses | ForEach-Object { '{0} {1}' -f $_.count, $_.cause }) -join '; ')))
        }
        if ($null -ne $timing.clearedSelections) { $lines.Add(('Selections cleared during the clicks: {0}.' -f $timing.clearedSelections)) }
        if (@($clicks | Where-Object { $null -ne $_.sentBeforeViewChange }).Count) {
            $crossed = @($report.sentBeforeViewChange | ForEach-Object { '{0} on {1}' -f $_.count, $_.pressedOn })
            $lines.Add(('Clicks sent before a table view change reached the screen and handled after it: {0}.' -f
                $(if ($crossed.Count) { $crossed -join '; ' } else { 0 })))
            # The hit test finds no row where a row is animating in. The table ignores such a click while the row is
            # still invisible and gives it the row laid out there after that.
            foreach ($outcome in @(@{ Selected = $true; Text = 'given the row laid out there' }, @{ Selected = $false; Text = 'that selected nothing' })) {
                $arriving = @($clicks | Where-Object { $_.pressedOn -eq 'no row' -and ($null -ne $_.selectedMs) -eq $outcome.Selected -and $null -ne $_.afterViewChangeMs })
                $lines.Add(('Clicks on no row, {0}: {1}; sent this many ms after the last table view change was committed: {2}.' -f $outcome.Text,
                    $arriving.Count, $(if ($arriving.Count) { (@($arriving.afterViewChangeMs | Sort-Object | ForEach-Object { '{0:F0}' -f $_ })) -join ', ' } else { '—' })))
            }
        }
        $lines.Add(('UI-thread cycles from press to rendered selection: median {0:F1}, p95 {1:F1}, worst {2:F1} megacycles.' -f
            $report.renderMegacycles.median, $report.renderMegacycles.p95, $report.renderMegacycles.worst))
        $lines.Add(('All-click feedback, including already-selected acknowledgements: p95 {0:F1} ms, worst {1:F1} ms.' -f
            $report.feedbackMs.p95, $report.feedbackMs.worst))
        $lines.Add(('Actual click cadence: median {0:F1} ms, worst {1:F1} ms; valid within 150±15 ms: {2}.' -f
            $report.clickCadenceMs.median, $report.clickCadenceMs.worst, $report.cadenceValid))
        foreach ($visit in ($clicks | Group-Object visit)) {
            $selected = Summary @($visit.Group.selectedMs | Where-Object { $null -ne $_ })
            $rendered = Summary @($visit.Group.renderMs | Where-Object { $null -ne $_ })
            $lines.Add(('Selection visit {0}: p95 selection {1:F1} ms; p95 render completion {2:F1} ms.' -f $visit.Name, $selected.p95, $rendered.p95))
        }
        $lines.Add('Feedback meets a one-refresh render-completion budget only when every click is selected and rendered within the frame budget. Physical display latency is reported separately.')
        $lines.Add(('One-refresh feedback budget met: {0}.' -f ($report.cadenceValid -and $report.invalidClickMatches -eq 0 -and $report.unselected -eq 0 -and
            $report.notRendered -eq 0 -and $report.feedbackMs.worst -le $period)))
    }
    if ($marqueePhase -and $report.marqueeSamples -lt 2) {
        $lines.Add(('Marquee smoothness unavailable: the failed interval contains only {0} samples.' -f $report.marqueeSamples))
    }
    if ($report.marquee) {
        $lines.Add(('Marquee advancing positions: {0:F1}/s; p95 gap {1:F1} ms, worst {2:F1} ms, estimated missed refreshes {3}.' -f
            $report.marquee.fps, $report.marquee.frameMs.p95, $report.marquee.frameMs.worst, $report.marquee.missedRefreshes))
        $lines.Add(('Marquee pacing: {0} holds longer than 1.5 refresh intervals; {1} stalls over 100 ms.' -f
            $report.marquee.longHolds, $report.marquee.stalls))
    }
    $lines.Add('')
    $lines.Add('During continuous motion, holds over 1.5 refresh intervals indicate missed animation opportunities. Idle window surfaces do not need to redraw every refresh. A dropped present can be replaced by a newer frame; read it alongside displayed frame holds.')
    $lines.Add('')
    if ($report.memory) {
        $m = $report.memory
        $lines.Add(('UI memory: sampled peak private {0:F1} MiB; process peak working set {1:F1} MiB; private start/end {2:F1}/{3:F1} MiB.' -f
            $m.peakPrivateMiB, $m.processPeakWorkingSetMiB, $m.startPrivateMiB, $m.endPrivateMiB))
        $lines.Add('')
        $lines.Add('| Memory interval | Private start MiB | Private peak MiB | Private end MiB | Private growth MiB | Working-set peak MiB |')
        $lines.Add('|---|---:|---:|---:|---:|---:|')
        $intervals = @($phases | Where-Object memory | ForEach-Object { [pscustomobject]@{ name = $_.name; memory = $_.memory } })
        $intervals += @($report.memoryPasses | Where-Object memory | ForEach-Object { [pscustomobject]@{ name = "Click pass $($_.visit)"; memory = $_.memory } })
        foreach ($interval in $intervals) {
            $m = $interval.memory
            $lines.Add(('| {0} | {1:F1} | {2:F1} | {3:F1} | {4:F1} | {5:F1} |' -f $interval.name,
                $m.startPrivateMiB, $m.peakPrivateMiB, $m.endPrivateMiB, $m.privateGrowthMiB, $m.peakWorkingSetMiB))
        }
        $lines.Add('')
    }
    $lines.Add('| Renderer operation elapsed time (journey, operation) | Count | Median ms | p95 ms | Worst ms |')
    $lines.Add('|---|---:|---:|---:|---:|')
    foreach ($measurement in $report.work) {
        $ms = $measurement.milliseconds
        $lines.Add(('| {0} | {1} | {2:F2} | {3:F2} | {4:F2} |' -f $measurement.name, $ms.count, $ms.median, $ms.p95, $ms.worst))
    }
    foreach ($outcome in $review.outcomes) { $lines.Add(($outcome | ConvertTo-Json -Compress -Depth 5)) }
    $lines.Add('')
    foreach ($limit in $report.limits) { $lines.Add("- $limit") }
    $lines | Set-Content -LiteralPath (Join-Path $Directory 'performance.md') -Encoding utf8NoBOM
    Write-Host ($lines -join [Environment]::NewLine)
}

if ($Analyze) { Report; return }
if (-not $WindowPath -or -not (Test-Path -LiteralPath $WindowPath -PathType Leaf)) {
    throw 'The Capture.ps1 performance journey supplies its capture window executable.'
}
if ($Replay) {
    $runtime = Get-Content -LiteralPath ([IO.Path]::ChangeExtension($WindowPath, '.runtimeconfig.json')) -Raw | ConvertFrom-Json
    if ($runtime.runtimeOptions.configProperties.'TinyTorrent.PerformanceReplay' -ne $true) {
        throw 'This binary predates offscreen replay. Build the capture app before using -Replay.'
    }
    if ($env:TINYTORRENT_PERFORMANCE_REPLAY -ne '1') { throw 'Capture.ps1 must enable replay in the diagnostic window.' }
}
if (-not $PresentMonPath) {
    $repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $PresentMonPath = Join-Path $repository 'artifacts/tools/PresentMon-2.3.1-x64.exe'
}
if (-not $InputOnly -and -not (Test-Path -LiteralPath $PresentMonPath)) { throw 'Pass -PresentMonPath for Intel PresentMon 2.3.1.' }
$ui = $null
$recorder = $null
$sender = $null
$stdout = $null
$stderr = $null
$failure = $null
$trace = "TinyTorrent-$([guid]::NewGuid().ToString('N'))"
$recordingSeconds = 120
$memorySamples = [Collections.Generic.List[object]]::new()
function SampleMemory {
    if ($ui.HasExited) { return }
    try {
        $ui.Refresh()
        $memorySamples.Add([pscustomobject]@{ qpc = [Diagnostics.Stopwatch]::GetTimestamp();
            workingSetBytes = $ui.WorkingSet64; privateBytes = $ui.PrivateMemorySize64; peakWorkingSetBytes = $ui.PeakWorkingSet64 })
    } catch { if (-not $ui.HasExited) { throw } }
}
function SaveMemory {
    ConvertTo-Json -InputObject $memorySamples.ToArray() -Depth 3 |
        Set-Content -LiteralPath (Join-Path $Directory 'memory.json') -Encoding utf8NoBOM
}
function StopSender {
    if (-not $script:sender) { return }
    try {
        if (-not $sender.HasExited) {
            $null = New-Item -ItemType File -Path (Join-Path $Directory 'input-stop') -Force
            if (-not $sender.WaitForExit(5000)) {
                Stop-Process -Id $sender.Id
                $sender.WaitForExit()
                throw 'The input driver did not exit after its stop signal.'
            }
        }
        $senderOut.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $Directory 'input-driver.log') -Encoding utf8NoBOM
        $senderError.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $Directory 'input-driver-error.log') -Encoding utf8NoBOM
        if ($sender.ExitCode -ne 0) { throw "The input/source driver exited with $($sender.ExitCode). See input-driver-error.log." }
    } finally { $sender.Dispose(); $script:sender = $null }
}
try {
    $null = New-Item -ItemType Directory -Path $Directory -Force
    if (-not $InputOnly) {
        $start = [Diagnostics.ProcessStartInfo]::new($PresentMonPath)
        $start.UseShellExecute = $ElevatedRecorder.IsPresent
        if ($ElevatedRecorder) { $start.Verb = 'runas'; $start.WindowStyle = 'Hidden' }
        else {
            $start.CreateNoWindow = $true
            $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
        }
        foreach ($argument in @('--process_name', [IO.Path]::GetFileName($WindowPath), '--qpc_time', '--v2_metrics', '--no_console_stats',
            '--output_file', (Join-Path $Directory 'frames.csv'), '--session_name', $trace,
            '--timed', "$recordingSeconds", '--terminate_after_timed', '--terminate_on_proc_exit')) { $start.ArgumentList.Add($argument) }
        $recorder = [Diagnostics.Process]::Start($start)
        if (-not $ElevatedRecorder) {
            $stdout = $recorder.StandardOutput.ReadToEndAsync(); $stderr = $recorder.StandardError.ReadToEndAsync()
        }
    }
    $sendStart = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $sendStart.UseShellExecute = $false
    $sendStart.CreateNoWindow = $true
    $sendStart.RedirectStandardOutput = $true; $sendStart.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'PerformanceInput.ps1'), '-Directory', $Directory, '-ParentPid', "$PID")) {
        $sendStart.ArgumentList.Add($argument)
    }
    $sender = [Diagnostics.Process]::Start($sendStart)
    $senderOut = $sender.StandardOutput.ReadToEndAsync(); $senderError = $sender.StandardError.ReadToEndAsync()
    $ui = Start-Process -FilePath $WindowPath -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $Directory 'window.log') -RedirectStandardError (Join-Path $Directory 'window-error.log')
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not (Test-Path -LiteralPath (Join-Path $Directory 'ready.json')) -or
        -not (Test-Path -LiteralPath (Join-Path $Directory 'input-ready'))) {
        SampleMemory
        if ($ui.HasExited -or $sender.HasExited -or ($recorder -and $recorder.HasExited) -or [DateTime]::UtcNow -ge $deadline) {
            throw "The benchmark window or recorder did not become ready. See $Directory."
        }
        Start-Sleep -Milliseconds 50
    }
    $ready = Get-Content -LiteralPath (Join-Path $Directory 'ready.json') -Raw | ConvertFrom-Json
    if ([bool]$ready.replay -ne $Replay.IsPresent) { throw 'The benchmark window and runner disagree about offscreen replay.' }
    if (-not $Replay) {
        $shell = New-Object -ComObject WScript.Shell
        try {
            if (-not $shell.AppActivate($ui.Id)) { throw 'Windows could not activate the diagnostic window.' }
        } finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    }
    # The first map present can create the CSV; its existence cannot gate input.
    Start-Sleep -Seconds 1
    if ($recorder -and $recorder.HasExited) { throw "PresentMon stopped during startup with $($recorder.ExitCode)." }
    $null = New-Item -ItemType File -Path (Join-Path $Directory 'go')
    # A hold run ends when the owner closes the window.
    $deadline = if ($Hold) { [DateTime]::MaxValue } else { [DateTime]::UtcNow.AddSeconds(120) }
    while (-not $ui.WaitForExit(100)) {
        SampleMemory
        $request = Join-Path $Directory 'screenshot-request'
        if (Test-Path -LiteralPath $request) {
            if ($Replay) { throw 'Offscreen replay must not request an image capture.' }
            $name = Get-Content -LiteralPath $request -Raw
            $image = Join-Path $Directory ($name + '.png')
            $capture = & winapp ui screenshot -a $ui.Id -o $image --capture-screen --json 2>&1
            if ($LASTEXITCODE -ne 0) { throw "The map screenshot failed: $capture" }
            Remove-Item -LiteralPath $request
            $null = New-Item -ItemType File -Path (Join-Path $Directory 'screenshot-done') -Force
        }
        if ($sender.HasExited) { throw 'The external input driver stopped before the benchmark completed.' }
        if ($recorder -and $recorder.HasExited) { throw 'The display recorder stopped before the benchmark completed.' }
        if ([DateTime]::UtcNow -ge $deadline) { throw 'The benchmark did not close within 120 seconds.' }
    }
    @{ processId = $ui.Id; exitCode = $ui.ExitCode; executable = $WindowPath } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Directory 'window-exit.json') -Encoding utf8NoBOM
    SaveMemory
    StopSender
    $review = ReadReview -AllowFailure
    if ($Hold) {
        Write-Host "Hold run closed: it measured nothing, so it wrote no performance report. Evidence: $Directory"
    }
    elseif ($InputOnly) {
        Report
        Write-Host "Input scenarios completed without a display recorder. Evidence: $Directory"
    }
    else {
        $deadline = $recorder.StartTime.ToUniversalTime().AddSeconds($recordingSeconds + 5)
        while (-not $recorder.WaitForExit(100)) {
            if ([DateTime]::UtcNow -ge $deadline) { throw 'The recorder did not finish its bounded capture window.' }
        }
        if ($recorder.ExitCode -ne 0) { throw "PresentMon exited with $($recorder.ExitCode)." }
        Report
    }
    if ($review.failure) { throw $review.failure }
    $ui
} catch {
    $failure = $_
    SaveMemory
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $Directory 'runner-error.log') -Encoding utf8NoBOM
    Write-Host $_
    throw
} finally {
    $cleanupFailure = $null
    try {
        if ($ui -and -not $ui.HasExited) {
            $null = $ui.CloseMainWindow()
            if (-not $ui.WaitForExit(5000)) { Stop-Process -Id $ui.Id; $ui.WaitForExit() }
        }
    } catch { $cleanupFailure = $_ }
    try { StopSender }
    catch { if (-not $cleanupFailure) { $cleanupFailure = $_ } }
    try {
        if ($recorder) {
            if (-not $recorder.HasExited) {
                Write-Host 'Waiting for the recorder safety timer to stop this failed recording; no further elevation is requested.'
                $deadline = $recorder.StartTime.ToUniversalTime().AddSeconds($recordingSeconds + 5)
                while (-not $recorder.WaitForExit(1000)) {
                    if ([DateTime]::UtcNow -ge $deadline) {
                        throw "The frame recorder is still running (PID $($recorder.Id)); see runner-error.log for the original failure."
                    }
                }
            }
            if ($stdout) { $stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $Directory 'recorder.log') -Encoding utf8NoBOM }
            if ($stderr) { $stderr.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $Directory 'recorder-error.log') -Encoding utf8NoBOM }
            $recorder.Dispose()
        }
    } catch { if (-not $cleanupFailure) { $cleanupFailure = $_ } }
    if ($cleanupFailure) {
        $cleanupFailure | Out-String | Set-Content -LiteralPath (Join-Path $Directory 'cleanup-error.log') -Encoding utf8NoBOM
        if (-not $failure) { throw $cleanupFailure }
        Write-Host "Cleanup also failed; see $(Join-Path $Directory 'cleanup-error.log'). The original failure is retained."
    }
}
