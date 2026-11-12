using Microsoft.UI.Xaml.Media;

namespace Syno;

/// <summary>
/// The icon font Windows provides, and each glyph the product draws from it.
/// </summary>
/// <remarks>
/// Windows 11 has Segoe Fluent Icons. Windows 10 has only Segoe MDL2 Assets, which draws most of
/// the same code points in its older style; Microsoft offers Segoe Fluent Icons as a download for
/// design and development only, so the product cannot ship it. A glyph Windows 10 lacks therefore
/// names a second code point for it. A name says what the icon means where the product uses it,
/// not what Microsoft calls the glyph.
/// </remarks>
public static class Segoe
{
    // Windows 11 is build 22000 and later, and every build of it has Segoe Fluent Icons.
    private static readonly bool Windows11 = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    private static FontFamily? _font;

    /// <summary>
    /// The installed icon font, for a <c>FontIcon</c> that draws these glyphs. It is made at first
    /// use, so code that runs without WinUI can still read the glyph names.
    /// </summary>
    public static FontFamily Font => _font ??= new(Windows11 ? "Segoe Fluent Icons" : "Segoe MDL2 Assets");

    public const string Activity = "\uE9D9";
    public const string AlignHorizontalSpaceBetween = "\uE9A6";
    public const string AlignLeft = "\uE8E4";
    public const string AppWindow = "\uECAA";
    public const string ArrowDown = "\uE74B";
    public const string ArrowDownToLine = "\uE896";
    public const string ArrowDownUp = "\uE8CB";
    public const string ArrowLeft = "\uE72B";
    public const string ArrowLeftRight = "\uE8AB";
    public const string ArrowRight = "\uE72A";
    public const string ArrowUp = "\uE74A";
    public const string ArrowUpToLine = "\uE898";
    public static readonly string Ban = Windows11 ? "\uE733" : "\uF140";
    public const string Bell = "\uEA8F";
    public const string BellMinus = "\uE7ED";
    public const string BellOff = "\uE7ED";
    public static readonly string BookOpenCheck = Windows11 ? "\uF28B" : "\uE9D5";
    public const string Building2 = "\uEC06";
    public const string BuildingComplex = "\uEC07";
    public const string Calculator = "\uE8EF";
    public const string Calendar = "\uE787";
    public const string CalendarClock = "\uEC92";
    public const string Captions = "\uED1E";
    public const string ChartLine = "\uE9D2";
    public const string Check = "\uE73E";
    public const string ChevronDown = "\uE70D";
    public const string ChevronLeft = "\uE76B";
    public const string ChevronRight = "\uE76C";
    public const string ChevronUp = "\uE70E";
    public const string ChevronsDownUp = "\uF165";
    public const string ChevronsUpDown = "\uECCD";
    public const string CircleAlert = "\uE783";
    public const string CircleCheck = "\uE930";
    public const string CircleFadingArrowUp = "\uE895";
    public const string CircleOff = "\uECE4";
    public const string CirclePlus = "\uECC8";
    public const string CircleQuestionMark = "\uE9CE";
    public static readonly string CircleSlash = Windows11 ? "\uE733" : "\uF140";
    public const string CircleX = "\uEA39";
    public const string Clapperboard = "\uE714";
    public const string ClipboardPaste = "\uE77F";
    public static readonly string Clock = Windows11 ? "\uE917" : "\uE823";
    public const string CodeXml = "\uE943";
    public const string Cog = "\uE713";
    public const string Columns2 = "\uE89A";

    /// <summary>
    /// A globe with its lower-right corner left open; draw <see cref="ConnectedCheck"/> over it to
    /// fill that corner.
    /// </summary>
    public const string Connected = "\uF385";

    /// <summary>The check that completes <see cref="Connected"/>, at the same position.</summary>
    public const string ConnectedCheck = "\uF386";
    public const string Copy = "\uE8C8";
    public static readonly string Cpu = Windows11 ? "\uEEA1" : "\uE950";
    public const string Database = "\uE8F1";
    public const string Disc = "\uE93C";
    public const string Download = "\uE896";
    public const string Ellipsis = "\uE712";
    public const string EllipsisVertical = "\uE784";
    public const string Eraser = "\uE75C";
    public const string EthernetPort = "\uE839";
    public const string ExternalLink = "\uE8A7";
    public const string Eye = "\uE890";
    public static readonly string EyeOff = Windows11 ? "\uED1A" : "\uE711";
    public const string File = "\uE7C3";
    public static readonly string FileArchive = Windows11 ? "\uF012" : "\uE7B8";
    public const string FileImage = "\uE91B";
    public const string FileMusic = "\uE8D6";
    public const string FilePlus = "\uE8E5";
    public const string FileSearch = "\uE773";
    public const string FileText = "\uE8A5";
    public const string FileVideoCamera = "\uE714";
    public const string Files = "\uEC50";
    public const string Film = "\uE8B2";
    public const string Folder = "\uE8B7";
    public const string FolderDown = "\uE896";
    public const string FolderInput = "\uE8DE";
    public const string FolderOpen = "\uE838";
    public const string FolderSearch = "\uF89A";
    public const string Funnel = "\uE71C";
    public const string FunnelX = "\uE71C";
    public const string Gauge = "\uEC4A";
    public const string Globe = "\uE774";
    public const string Globe2 = "\uE909";
    public const string GraphStar = "\uF003";
    public const string Grid3x3 = "\uF0E2";
    public const string Handshake = "\uE8FA";
    public const string HardDrive = "\uEDA2";
    public const string Hash = "\uE928";
    public const string History = "\uE81C";
    public const string Hourglass = "\uE916";
    public const string House = "\uE80F";
    public const string Info = "\uE946";
    public const string KeyRound = "\uE8D7";
    public const string Languages = "\uF2B7";
    public const string Lightbulb = "\uE706";
    public const string LightbulbOff = "\uE708";
    public const string Limited = "\uEC48";
    public const string Link = "\uE71B";
    public const string List = "\uEA37";
    public const string ListChecks = "\uE9D5";
    public const string ListMusic = "\uE90B";
    public const string ListOrdered = "\uE8FD";
    public static readonly string ListTodo = Windows11 ? "\uF7EC" : "\uE9D5";
    public const string ListX = "\uECC9";
    public const string LocateFixed = "\uE81D";
    public const string Lock = "\uE72E";
    public const string Magnet = "\uE71B";
    public const string MapPin = "\uE707";
    public const string Maximize = "\uE740";
    public const string Maximize2 = "\uE740";
    public static readonly string MemoryStick = Windows11 ? "\uEEA0" : "\uE950";
    public const string Merge = "\uEA3C";
    public const string MessageSquare = "\uE8BD";
    public const string Minimize2 = "\uE73F";
    public const string Minus = "\uE738";
    public const string Monitor = "\uE7F4";
    public static readonly string Move = Windows11 ? "\uE7C2" : "\uE759";
    public const string Music = "\uEC4F";
    public const string Network = "\uE968";
    public const string Package = "\uE7B8";
    public const string Palette = "\uE790";
    public const string PanelBottom = "\uE90E";
    public const string PanelLeftClose = "\uE89F";
    public const string PanelLeftOpen = "\uE8A0";
    public const string PanelTop = "\uE737";
    public const string PartlyLimited = "\uEC49";
    public const string Pause = "\uE769";
    public const string Pencil = "\uE70F";
    public const string Pin = "\uE718";
    public const string Play = "\uE768";
    public const string Plug = "\uE703";
    public const string PlugZap = "\uE703";
    public const string Plus = "\uE710";
    public const string Power = "\uE7E8";
    public const string Puzzle = "\uEA86";
    public const string Radar = "\uEB44";
    public const string RadioButton = "\uECCA";
    public const string RadioButtonOn = "\uECCB";
    public const string RadioTower = "\uEC05";
    public const string RefreshCw = "\uE72C";
    public const string RotateCcw = "\uE777";
    public const string RotateCw = "\uE72C";
    public const string Route = "\uEF90";
    public const string RulerDimensionLine = "\uED5E";
    public const string Save = "\uE74E";
    public const string Scale = "\uE799";
    public const string ScanSearch = "\uE71E";
    public const string ScrollText = "\uF000";
    public const string Search = "\uE721";
    public const string Settings = "\uE713";
    public const string Shield = "\uEA18";
    public const string ShieldCheck = "\uEA18";
    public const string ShieldOff = "\uE785";
    public static readonly string ShieldPlus = Windows11 ? "\uF5B4" : "\uE72E";
    public const string SlidersHorizontal = "\uE9E9";
    public const string Sprout = "\uE8BE";
    public const string Square = "\uE739";
    public const string SquareCheck = "\uE73A";
    public const string SquareDashedMousePointer = "\uEF20";
    public const string SquareMinus = "\uF16E";
    public const string Star = "\uE734";
    public const string Tag = "\uE8EC";
    public const string Tags = "\uE8EC";
    public const string Ticket = "\uE8C7";
    public const string Trash2 = "\uE74D";
    public const string TriangleAlert = "\uE7BA";
    public const string Turtle = "\uEA79";
    public const string Tv = "\uE7F4";
    public const string Undo2 = "\uE7A7";
    public const string UnfoldHorizontal = "\uE740";
    public const string Unplug = "\uF384";
    public const string Upload = "\uE898";
    public const string Users = "\uE716";
    public const string UsersRoundCollaboration = "\uE902";
    public const string Wrench = "\uE90F";
    public const string X = "\uE711";
    public const string Zap = "\uE945";
}
