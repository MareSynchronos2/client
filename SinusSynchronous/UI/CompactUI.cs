﻿using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using Microsoft.Extensions.Logging;
using SinusSynchronous.API.Data.Extensions;
using SinusSynchronous.API.Dto.Group;
using SinusSynchronous.Interop.Ipc;
using SinusSynchronous.PlayerData.Handlers;
using SinusSynchronous.PlayerData.Pairs;
using SinusSynchronous.Services;
using SinusSynchronous.Services.Mediator;
using SinusSynchronous.Services.ServerConfiguration;
using SinusSynchronous.SinusConfiguration;
using SinusSynchronous.UI.Components;
using SinusSynchronous.UI.Handlers;
using SinusSynchronous.WebAPI;
using SinusSynchronous.WebAPI.Files;
using SinusSynchronous.WebAPI.Files.Models;
using SinusSynchronous.WebAPI.SignalR.Utils;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;

namespace SinusSynchronous.UI;

public class CompactUi : WindowMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly SinusConfigService _configService;
    private readonly ConcurrentDictionary<GameObjectHandler, Dictionary<string, FileDownloadStatus>> _currentDownloads = new();
    private readonly DrawEntityFactory _drawEntityFactory;
    private readonly FileUploadManager _fileTransferManager;
    private readonly PairManager _pairManager;
    private readonly SelectTagForPairUi _selectGroupForPairUi;
    private readonly SelectPairForTagUi _selectPairsForGroupUi;
    private readonly IpcManager _ipcManager;
    private readonly ServerConfigurationManager _serverConfigManager;
    private readonly TopTabMenu _tabMenu;
    private readonly TagHandler _tagHandler;
    private readonly UiSharedService _uiSharedService;
    private readonly CharacterAnalyzer _characterAnalyzer;
    private readonly PlayerPerformanceConfigService _playerPerformanceConfigService;
    private readonly ServerConfigService _serverConfigService;
    private Dictionary<ObjectKind, Dictionary<string, CharacterAnalyzer.FileDataEntry>>? _cachedAnalysis;
    private bool _hasUpdate = false;
    private List<IDrawFolder> _drawFolders;
    private Pair? _lastAddedUser;
    private string _lastAddedUserComment = string.Empty;
    private Vector2 _lastPosition = Vector2.One;
    private Vector2 _lastSize = Vector2.One;
    private int _secretKeyIdx = -1;
    private bool _showModalForUserAddition;
    private float _transferPartHeight;
    private bool _wasOpen;
    private float _windowContentWidth;
    private bool _showMultiServerSelect = false;

    public CompactUi(ILogger<CompactUi> logger, UiSharedService uiShared, SinusConfigService configService, ApiController apiController, PairManager pairManager,
        ServerConfigurationManager serverConfigManager, SinusMediator mediator, FileUploadManager fileTransferManager,
        TagHandler tagHandler, DrawEntityFactory drawEntityFactory, SelectTagForPairUi selectTagForPairUi, SelectPairForTagUi selectPairForTagUi,
        PerformanceCollectorService performanceCollectorService, IpcManager ipcManager, CharacterAnalyzer characterAnalyzer, PlayerPerformanceConfigService playerPerformanceConfigService, ServerConfigService serverConfigService)
        : base(logger, mediator, "###SinusSynchronousMainUI", performanceCollectorService)
    {
        _uiSharedService = uiShared;
        _configService = configService;
        _apiController = apiController;
        _pairManager = pairManager;
        _serverConfigManager = serverConfigManager;
        _fileTransferManager = fileTransferManager;
        _tagHandler = tagHandler;
        _drawEntityFactory = drawEntityFactory;
        _selectGroupForPairUi = selectTagForPairUi;
        _selectPairsForGroupUi = selectPairForTagUi;
        _ipcManager = ipcManager;
        _characterAnalyzer = characterAnalyzer;
        _playerPerformanceConfigService = playerPerformanceConfigService;
        _serverConfigService = serverConfigService;
        _tabMenu = new TopTabMenu(Mediator, _apiController, _pairManager, _uiSharedService, _serverConfigManager);

        CheckForCharacterAnalysis();

        Mediator.Subscribe<CharacterDataAnalyzedMessage>(this, _ =>
        {
            _hasUpdate = true;
        });

        AllowPinning = false;
        AllowClickthrough = false;
        TitleBarButtons = new()
        {
            new TitleBarButton()
            {
                Icon = FontAwesomeIcon.Cog,
                Click = (msg) =>
                {
                    Mediator.Publish(new UiToggleMessage(typeof(SettingsUi)));
                },
                IconOffset = new(2,1),
                ShowTooltip = () =>
                {
                    ImGui.BeginTooltip();
                    ImGui.Text("Open Sinus Settings");
                    ImGui.EndTooltip();
                }
            },
            new TitleBarButton()
            {
                Icon = FontAwesomeIcon.Book,
                Click = (msg) =>
                {
                    Mediator.Publish(new UiToggleMessage(typeof(EventViewerUI)));
                },
                IconOffset = new(2,1),
                ShowTooltip = () =>
                {
                    ImGui.BeginTooltip();
                    ImGui.Text("Open Sinus Event Viewer");
                    ImGui.EndTooltip();
                }
            }
        };

        _drawFolders = GetDrawFolders().ToList();

#if DEBUG
        string dev = "Dev Build";
        var ver = Assembly.GetExecutingAssembly().GetName().Version!;
        WindowName = $"Sinus Synchronous {dev} ({ver.Major}.{ver.Minor}.{ver.Build})###SinusSynchronousMainUI";
        Toggle();
#else
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        WindowName = "Sinus Synchronous " + ver.Major + "." + ver.Minor + "." + ver.Build + "###SinusSynchronousMainUI";
#endif
        Mediator.Subscribe<SwitchToMainUiMessage>(this, (_) => IsOpen = true);
        Mediator.Subscribe<SwitchToIntroUiMessage>(this, (_) => IsOpen = false);
        Mediator.Subscribe<ToggleServerSelectMessage>(this, (_) => ToggleMultiServerSelect());
        Mediator.Subscribe<CutsceneStartMessage>(this, (_) => UiSharedService_GposeStart());
        Mediator.Subscribe<CutsceneEndMessage>(this, (_) => UiSharedService_GposeEnd());
        Mediator.Subscribe<DownloadStartedMessage>(this, (msg) => _currentDownloads[msg.DownloadId] = msg.DownloadStatus);
        Mediator.Subscribe<DownloadFinishedMessage>(this, (msg) => _currentDownloads.TryRemove(msg.DownloadId, out _));
        Mediator.Subscribe<RefreshUiMessage>(this, (msg) => _drawFolders = GetDrawFolders().ToList());

        Flags |= ImGuiWindowFlags.NoDocking;

        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new Vector2(375, 400),
            MaximumSize = new Vector2(600, 2000),
        };
    }

    protected override void DrawInternal()
    {
        _windowContentWidth = UiSharedService.GetWindowContentRegionWidth();
        //if (!_apiController.IsCurrentVersion)
        //{
        //    var ver = _apiController.CurrentClientVersion;
        //    var unsupported = "UNSUPPORTED VERSION";
        //    using (_uiSharedService.UidFont.Push())
        //    {
        //        var uidTextSize = ImGui.CalcTextSize(unsupported);
        //        ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMax().X + ImGui.GetWindowContentRegionMin().X) / 2 - uidTextSize.X / 2);
        //        ImGui.AlignTextToFramePadding();
        //        ImGui.TextColored(ImGuiColors.DalamudRed, unsupported);
        //    }
        //    UiSharedService.ColorTextWrapped($"Your Sinus Synchronous installation is out of date, the current version is {ver.Major}.{ver.Minor}.{ver.Build}. " +
        //        $"It is highly recommended to keep Sinus Synchronous up to date. Open /xlplugins and update the plugin.", ImGuiColors.DalamudRed);
        //}

        if (!_ipcManager.Initialized)
        {
            var unsupported = "MISSING ESSENTIAL PLUGINS";

            using (_uiSharedService.UidFont.Push())
            {
                var uidTextSize = ImGui.CalcTextSize(unsupported);
                ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMax().X + ImGui.GetWindowContentRegionMin().X) / 2 - uidTextSize.X / 2);
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(ImGuiColors.DalamudRed, unsupported);
            }
            var penumAvailable = _ipcManager.Penumbra.APIAvailable;
            var glamAvailable = _ipcManager.Glamourer.APIAvailable;

            UiSharedService.ColorTextWrapped($"One or more Plugins essential for Sinus operation are unavailable. Enable or update following plugins:", ImGuiColors.DalamudRed);
            using var indent = ImRaii.PushIndent(10f);
            if (!penumAvailable)
            {
                UiSharedService.TextWrapped("Penumbra");
                _uiSharedService.BooleanToColoredIcon(penumAvailable, true);
            }
            if (!glamAvailable)
            {
                UiSharedService.TextWrapped("Glamourer");
                _uiSharedService.BooleanToColoredIcon(glamAvailable, true);
            }
            ImGui.Separator();
        }

        DrawMultiServerSection();

        using (ImRaii.PushId("serverstatus")) DrawServerStatus();
        ImGui.Separator();

        if (_playerPerformanceConfigService.Current.ShowPlayerPerformanceInMainUi)
        {
            using (ImRaii.PushId("modload")) DrawModLoad();
        }

        using (ImRaii.PushId("global-topmenu")) _tabMenu.Draw();

        ImGui.BeginDisabled(!_apiController.AnyServerConnected);

        if (!_tabMenu.IsUserConfigTabSelected)
        {
            using (ImRaii.PushId("pairlist")) DrawPairs();
            ImGui.Separator();
        }
        else
        {
            using (ImRaii.PushId("pairlist")) DrawEmptyPairs();
            ImGui.Separator();
        }

        ImGui.Separator();
        float pairlistEnd = ImGui.GetCursorPosY();
        using (ImRaii.PushId("transfers")) DrawTransfers();
        _transferPartHeight = ImGui.GetCursorPosY() - pairlistEnd - ImGui.GetTextLineHeight();
        using (ImRaii.PushId("group-user-popup")) _selectPairsForGroupUi.Draw(_pairManager.DirectPairs);
        using (ImRaii.PushId("grouping-popup")) _selectGroupForPairUi.Draw();
        ImGui.EndDisabled();

        if (_configService.Current.OpenPopupOnAdd && _pairManager.LastAddedUser != null)
        {
            _lastAddedUser = _pairManager.LastAddedUser;
            _pairManager.LastAddedUser = null;
            ImGui.OpenPopup("Set Notes for New User");
            _showModalForUserAddition = true;
            _lastAddedUserComment = string.Empty;
        }

        if (ImGui.BeginPopupModal("Set Notes for New User", ref _showModalForUserAddition, UiSharedService.PopupWindowFlags))
        {
            if (_lastAddedUser == null)
            {
                _showModalForUserAddition = false;
            }
            else
            {
                UiSharedService.TextWrapped($"You have successfully added {_lastAddedUser.UserData.AliasOrUID}. Set a local note for the user in the field below:");
                ImGui.InputTextWithHint("##noteforuser", $"Note for {_lastAddedUser.UserData.AliasOrUID}", ref _lastAddedUserComment, 100);
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.Save, "Save Note"))
                {
                    _serverConfigManager.SetNoteForUid(_lastAddedUser.ServerIndex, _lastAddedUser.UserData.UID, _lastAddedUserComment);
                    _lastAddedUser = null;
                    _lastAddedUserComment = string.Empty;
                    _showModalForUserAddition = false;
                }
            }
            UiSharedService.SetScaledWindowSize(275);
            ImGui.EndPopup();
        }

        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        if (_lastSize != size || _lastPosition != pos)
        {
            _lastSize = size;
            _lastPosition = pos;
            Mediator.Publish(new CompactUiChange(_lastSize, _lastPosition));
        }
    }

    private void DrawEmptyPairs()
    {
        var ySize = _transferPartHeight == 0
            ? 1
            : (ImGui.GetWindowContentRegionMax().Y - ImGui.GetWindowContentRegionMin().Y
                + ImGui.GetTextLineHeight() - ImGui.GetStyle().WindowPadding.Y - ImGui.GetStyle().WindowBorderSize) - _transferPartHeight - ImGui.GetCursorPosY();

        ImGui.BeginChild("list", new Vector2(_windowContentWidth, ySize), border: false);
        ImGui.EndChild();
    }

    private void DrawPairs()
    {
        var ySize = _transferPartHeight == 0
            ? 1
            : (ImGui.GetWindowContentRegionMax().Y - ImGui.GetWindowContentRegionMin().Y
                + ImGui.GetTextLineHeight() - ImGui.GetStyle().WindowPadding.Y - ImGui.GetStyle().WindowBorderSize) - _transferPartHeight - ImGui.GetCursorPosY();

        ImGui.BeginChild("list", new Vector2(_windowContentWidth, ySize), border: false);

        foreach (var item in _drawFolders)
        {
            item.Draw();
        }

        ImGui.EndChild();
    }

    private void DrawServerStatus()
    {
        if (_apiController.ConnectedServerIndexes.Length > 1)
        {
            using (_uiSharedService.UidFont.Push())
            {
                var onlineText = _apiController.AnyServerConnected ? "Online" : "Offline";
                var origTextSize = ImGui.CalcTextSize(onlineText);
                ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X) / 2 - (origTextSize.X / 2));
                ImGui.TextColored(_apiController.AnyServerConnected ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudRed, onlineText);
            }

            DrawServerStatusTooltipAndToggle();
        }
        else
        {
            using (ImRaii.PushId("singleserveruid")) DrawUIDHeader(_secretKeyIdx == -1 ? 0 : _secretKeyIdx);
            ImGui.Separator();
        }

        if (_apiController.AnyServerConnected)
        {
            var userCount = _apiController.OnlineUsers.ToString(CultureInfo.InvariantCulture);
            var userSize = ImGui.CalcTextSize(userCount);
            var textSize = ImGui.CalcTextSize("Users Online");

            ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMin().X + UiSharedService.GetWindowContentRegionWidth()) / 2 - (userSize.X + textSize.X) / 2 - ImGui.GetStyle().ItemSpacing.X / 2);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(ImGuiColors.ParsedGreen, userCount);
            DrawServerStatusTooltipAndToggle();

            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Users Online");
            DrawServerStatusTooltipAndToggle();
        }
        else
        {
            ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMin().X + UiSharedService.GetWindowContentRegionWidth() / 2 - ImGui.CalcTextSize("Not connected to any server").X / 2);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(ImGuiColors.DalamudRed, "Not connected to any server");
            DrawServerStatusTooltipAndToggle();
        }
    }

    private void DrawUIDHeader(int serverId)
    {
        var uidText = GetUidTextMultiServer(serverId);
        var uidColor = GetUidColorByServer(serverId);

        using (_uiSharedService.UidFont.Push())
        {
            var uidTextSize = ImGui.CalcTextSize(uidText);
            ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X) / 2 - (uidTextSize.X / 2));
            ImGui.TextColored(uidColor, uidText);
        }

        if (_apiController.AnyServerConnected)
        {
            var currentDisplayName = _apiController.GetDisplayNameByServer(serverId);
            var currentUid = _apiController.GetUidByServer(serverId);
            if (ImGui.IsItemClicked())
            {
                ImGui.SetClipboardText(currentDisplayName);
            }
            UiSharedService.AttachToolTip("Click to copy");

            if (!string.Equals(currentDisplayName, currentUid, StringComparison.Ordinal))
            {
                var origTextSize = ImGui.CalcTextSize(currentDisplayName);
                ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X) / 2 - (origTextSize.X / 2));
                ImGui.TextColored(uidColor, currentDisplayName);
                if (ImGui.IsItemClicked())
                {
                    ImGui.SetClipboardText(currentDisplayName);
                }
                UiSharedService.AttachToolTip("Click to copy");
            }
        }
        else
        {
            UiSharedService.ColorTextWrapped(GetServerErrorByServer(serverId), uidColor);
        }
    }

    private void DrawServerStatusTooltipAndToggle()
    {
        if (ImGui.IsItemClicked())
        {
            ToggleMultiServerSelect();
        }
        UiSharedService.AttachToolTip("Manage service connections");
    }

    private void ToggleMultiServerSelect()
    {
        _showMultiServerSelect = !_showMultiServerSelect;
    }

    private void DrawMultiServerSection()
    {
        if (_showMultiServerSelect)
        {
            using (ImRaii.PushId("multiserversection"))
            {
                var mainPos = ImGui.GetWindowPos();
                var mainSize = ImGui.GetWindowSize();
                ImGui.SetNextWindowPos(new Vector2(mainPos.X + mainSize.X + 5, mainPos.Y), ImGuiCond.Always);
                ImGui.SetNextWindowSize(new Vector2(400, 400), ImGuiCond.Once);

                if (ImGui.Begin("MultiServerSidePanel", ref _showMultiServerSelect, ImGuiWindowFlags.NoTitleBar))
                {
                    DrawMultiServerInterfaceTable();
                    ImGui.End();
                }
            }
        }
    }

    private void DrawMultiServerInterfaceTable()
    {
        if (ImGui.BeginTable("MultiServerInterface", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn($" Server Name", ImGuiTableColumnFlags.None, 4);
            ImGui.TableSetupColumn($"My User ID", ImGuiTableColumnFlags.None, 2);
            ImGui.TableSetupColumn($"Users", ImGuiTableColumnFlags.None, 1);
            ImGui.TableSetupColumn($"Visible", ImGuiTableColumnFlags.None, 1);
            ImGui.TableSetupColumn($"Connection", ImGuiTableColumnFlags.None, 1);

            ImGui.TableHeadersRow();

            var serverList = _serverConfigManager.GetServerInfo();

            foreach (var server in serverList)
            {
                ImGui.TableNextColumn();
                DrawServerName(server.Id, server.Name, server.ServerUri);

                ImGui.TableNextColumn();
                DrawMultiServerUID(server.Id);

                ImGui.TableNextColumn();
                DrawOnlineUsers(server.Id);

                ImGui.TableNextColumn();
                DrawVisiblePairs(server.Id);

                ImGui.TableNextColumn();
                DrawMultiServerConnectButton(server.Id, server.Name);

                ImGui.TableNextRow();
            }

            ImGui.EndTable();
        }
    }

    private void DrawServerName(int serverId, string serverName, string serverUri)
    {
        if (_apiController.ConnectedServerIndexes.Any(p => p == serverId))
        {
            ImGui.TextColored(ImGuiColors.ParsedGreen, serverName);
            if (ImGui.IsItemClicked())
            {
                _serverConfigManager.SelectServer(serverId);
            }
        }
        else
            ImGui.TextUnformatted(serverName);

        if (!string.IsNullOrEmpty(serverUri))
            UiSharedService.AttachToolTip(serverUri);
    }

    private void DrawMultiServerUID(int serverId)
    {
        var textColor = GetUidColorByServer(serverId);
        if (_apiController.IsServerConnected(serverId))
        {
            var uidText = GetUidTextMultiServer(serverId);
            var uid = _apiController.GetUidByServer(serverId);
            var displayName = _apiController.GetDisplayNameByServer(serverId);
            ImGui.TextColored(textColor, uidText);

            if (ImGui.IsItemClicked())
            {
                ImGui.SetClipboardText(displayName);
            }
            UiSharedService.AttachToolTip("Click to copy");

            if (!string.Equals(displayName, uid, StringComparison.Ordinal))
            {
                ImGui.TextColored(textColor, displayName);
                if (ImGui.IsItemClicked())
                {
                    ImGui.SetClipboardText(displayName);
                }
                UiSharedService.AttachToolTip("Click to copy");
            }
        }
        else if (_apiController.IsServerAlive(serverId))
        {
            var serverError = GetServerErrorByServer(serverId);
            UiSharedService.ColorTextWrapped("Offline", ImGuiColors.DalamudYellow);

            if (!string.IsNullOrEmpty(serverError))
                UiSharedService.AttachToolTip(serverError);
        }
        else
        {
            var serverError = GetServerErrorByServer(serverId);
            UiSharedService.ColorTextWrapped("Offline", ImGuiColors.DalamudRed);

            if (!string.IsNullOrEmpty(serverError))
                UiSharedService.AttachToolTip(serverError);
        }
    }

    private void DrawMultiServerConnectButton(int serverId, string serverName)
    {
        bool isConnectingOrConnected = _apiController.IsServerConnected(serverId);
        var color = UiSharedService.GetBoolColor(!isConnectingOrConnected);
        var connectedIcon = isConnectingOrConnected ? FontAwesomeIcon.Unlink : FontAwesomeIcon.Link;

        using (ImRaii.PushColor(ImGuiCol.Text, color))
        {
            if (_uiSharedService.IconButton(connectedIcon, serverId))
            {
                if (_apiController.IsServerConnected(serverId))
                {
                    _serverConfigManager.CurrentServer.FullPause = true;
                    _serverConfigManager.Save();
                    _ = _apiController.PauseConnectionAsync(serverId);
                }
                else
                {
                    _serverConfigManager.SelectServer(serverId);
                    _serverConfigManager.CurrentServer.FullPause = false;
                    _serverConfigManager.Save();
                    _ = _apiController.CreateConnectionsAsync(serverId);
                }
            }
        }

        UiSharedService.AttachToolTip(isConnectingOrConnected ?
           "Disconnect from " + serverName :
           "Connect to " + serverName);

    }

    private void DrawOnlineUsers(int serverId)
    {
        if (_apiController.IsServerConnected(serverId))
            ImGui.TextColored(ImGuiColors.ParsedGreen, _apiController.GetOnlineUsersForServer(serverId).ToString(CultureInfo.InvariantCulture));
        else
            ImGui.TextColored(ImGuiColors.DalamudRed, string.Empty);
    }

    private void DrawVisiblePairs(int serverId)
    {
        if (_apiController.IsServerConnected(serverId))
            ImGui.TextColored(ImGuiColors.ParsedGreen, _pairManager.GetVisibleUserCount(serverId).ToString(CultureInfo.InvariantCulture));
        else
            ImGui.TextColored(ImGuiColors.DalamudRed, string.Empty);
    }

    private string GetUidTextMultiServer(int serverId)
    {
        return _apiController.GetServerState(serverId) switch
        {
            ServerState.Connected => _apiController.GetUidByServer(serverId),
            _ => "Offline"
        };
    }

    private void DrawModLoad()
    {
        CheckForCharacterAnalysis();

        if (_cachedAnalysis == null)
        {
            return;
        }

        var config = _playerPerformanceConfigService.Current;

        var playerLoadMemory = _cachedAnalysis.Sum(c => c.Value.Sum(c => c.Value.OriginalSize));
        var playerLoadTriangles = _cachedAnalysis.Sum(c => c.Value.Sum(c => c.Value.Triangles));

        ImGui.TextUnformatted("Character Load Data");
        ImGui.TextUnformatted("Mem.:");
        ImGui.SameLine();
        ImGui.TextUnformatted($"{UiSharedService.ByteToString(playerLoadMemory)}");

        if (config.VRAMSizeAutoPauseThresholdMiB > 0)
        {
            var _playerLoadMemoryKiB = playerLoadMemory / 1024;
            var vramWarningThreshold = config.VRAMSizeWarningThresholdMiB * 1024;
            var vramAutoPauseThreshold = config.VRAMSizeAutoPauseThresholdMiB * 1024;
            var warning = false;
            var alert = false;

            if (_playerLoadMemoryKiB > vramWarningThreshold)
                warning = true;

            if (_playerLoadMemoryKiB > vramAutoPauseThreshold)
                alert = true;

            ImGuiHelpers.ScaledRelativeSameLine(180, ImGui.GetStyle().ItemSpacing.X);
            var calculatedRam = (float)_playerLoadMemoryKiB / (vramAutoPauseThreshold);

            DrawProgressBar(calculatedRam, "VRAM usage", warning, alert);
        }

        ImGui.TextUnformatted("Tri.:");
        ImGui.SameLine();
        ImGui.TextUnformatted($"{playerLoadTriangles}");

        if (config.TrisAutoPauseThresholdThousands > 0)
        {
            var warning = false;
            if (playerLoadTriangles > config.TrisWarningThresholdThousands * 1000)
                warning = true;

            var alert = false;
            if (playerLoadTriangles > config.TrisAutoPauseThresholdThousands * 1000)
                alert = true;

            ImGuiHelpers.ScaledRelativeSameLine(180, ImGui.GetStyle().ItemSpacing.X);
            var calculatedTriangles = ((float)playerLoadTriangles / (config.TrisAutoPauseThresholdThousands * 1000));

            DrawProgressBar(calculatedTriangles, "Triangle count", warning, alert);
        }

        ImGui.Separator();
    }

    private static void DrawProgressBar(float value, string tooltipText, bool warning = false, bool alert = false)
    {
        float width = Math.Max(170, ImGui.GetContentRegionAvail().X);
        var progressBarSize = new Vector2(width, 20);

        if (warning)
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
        else if (alert)
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
        else
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.HealerGreen);

        ImGui.ProgressBar(value, progressBarSize);
        UiSharedService.AttachToolTip($"{MathF.Round(value * 100, 2)}% {tooltipText}.");
        ImGui.PopStyleColor();
    }

    private void DrawTransfers()
    {
        var currentUploads = _fileTransferManager.CurrentUploads.ToList();
        ImGui.AlignTextToFramePadding();
        _uiSharedService.IconText(FontAwesomeIcon.Upload);
        ImGui.SameLine(35 * ImGuiHelpers.GlobalScale);

        if (currentUploads.Any())
        {
            var totalUploads = currentUploads.Count;

            var doneUploads = currentUploads.Count(c => c.IsTransferred);
            var totalUploaded = currentUploads.Sum(c => c.Transferred);
            var totalToUpload = currentUploads.Sum(c => c.Total);

            ImGui.TextUnformatted($"{doneUploads}/{totalUploads}");
            var uploadText = $"({UiSharedService.ByteToString(totalUploaded)}/{UiSharedService.ByteToString(totalToUpload)})";
            var textSize = ImGui.CalcTextSize(uploadText);
            ImGui.SameLine(_windowContentWidth - textSize.X);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(uploadText);
        }
        else
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("No uploads in progress");
        }

        var currentDownloads = _currentDownloads.SelectMany(d => d.Value.Values).ToList();
        ImGui.AlignTextToFramePadding();
        _uiSharedService.IconText(FontAwesomeIcon.Download);
        ImGui.SameLine(35 * ImGuiHelpers.GlobalScale);

        if (currentDownloads.Any())
        {
            var totalDownloads = currentDownloads.Sum(c => c.TotalFiles);
            var doneDownloads = currentDownloads.Sum(c => c.TransferredFiles);
            var totalDownloaded = currentDownloads.Sum(c => c.TransferredBytes);
            var totalToDownload = currentDownloads.Sum(c => c.TotalBytes);

            ImGui.TextUnformatted($"{doneDownloads}/{totalDownloads}");
            var downloadText =
                $"({UiSharedService.ByteToString(totalDownloaded)}/{UiSharedService.ByteToString(totalToDownload)})";
            var textSize = ImGui.CalcTextSize(downloadText);
            ImGui.SameLine(_windowContentWidth - textSize.X);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(downloadText);
        }
        else
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("No downloads in progress");
        }
    }

    private IEnumerable<IDrawFolder> GetDrawFolders()
    {
        List<IDrawFolder> drawFolders = [];

        var allPairs = _pairManager.PairsWithGroups
            .ToDictionary(k => k.Key, k => k.Value);
        var filteredPairs = allPairs
            .Where(p =>
            {
                if (_tabMenu.Filter.IsNullOrEmpty()) return true;
                return p.Key.UserData.AliasOrUID.Contains(_tabMenu.Filter, StringComparison.OrdinalIgnoreCase) ||
                       (p.Key.GetNote()?.Contains(_tabMenu.Filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                       (p.Key.PlayerName?.Contains(_tabMenu.Filter, StringComparison.OrdinalIgnoreCase) ?? false);
            })
            .ToDictionary(k => k.Key, k => k.Value);

        string? AlphabeticalSort(KeyValuePair<Pair, List<GroupFullInfoDto>> u)
            => (_configService.Current.ShowCharacterNameInsteadOfNotesForVisible && !string.IsNullOrEmpty(u.Key.PlayerName)
                    ? (_configService.Current.PreferNotesOverNamesForVisible ? u.Key.GetNote() : u.Key.PlayerName)
                    : (u.Key.GetNote() ?? u.Key.UserData.AliasOrUID));
        bool FilterOnlineOrPausedSelf(KeyValuePair<Pair, List<GroupFullInfoDto>> u)
            => (u.Key.IsOnline || (!u.Key.IsOnline && !_configService.Current.ShowOfflineUsersSeparately)
                    || u.Key.UserPair.OwnPermissions.IsPaused());
        Dictionary<Pair, List<GroupFullInfoDto>> BasicSortedDictionary(IEnumerable<KeyValuePair<Pair, List<GroupFullInfoDto>>> u)
            => u.OrderByDescending(u => u.Key.IsVisible)
                .ThenByDescending(u => u.Key.IsOnline)
                .ThenBy(AlphabeticalSort, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(u => u.Key, u => u.Value);
        ImmutableList<Pair> ImmutablePairList(IEnumerable<KeyValuePair<Pair, List<GroupFullInfoDto>>> u)
            => u.Select(k => k.Key).ToImmutableList();
        bool FilterVisibleUsers(KeyValuePair<Pair, List<GroupFullInfoDto>> u)
            => u.Key.IsVisible
                && (_configService.Current.ShowSyncshellUsersInVisible || !(!_configService.Current.ShowSyncshellUsersInVisible && !u.Key.IsDirectlyPaired));
        bool FilterTagusers(KeyValuePair<Pair, List<GroupFullInfoDto>> u, string tag)
            => u.Key.IsDirectlyPaired && !u.Key.IsOneSidedPair && _tagHandler.HasTag(u.Key.UserData.UID, tag);
        bool FilterGroupUsers(KeyValuePair<Pair, List<GroupFullInfoDto>> u, GroupFullInfoDto group)
            => u.Value.Exists(g => string.Equals(g.GID, group.GID, StringComparison.Ordinal));
        bool FilterNotTaggedUsers(KeyValuePair<Pair, List<GroupFullInfoDto>> u)
            => u.Key.IsDirectlyPaired && !u.Key.IsOneSidedPair && !_tagHandler.HasAnyTag(u.Key.UserData.UID);
        bool FilterOfflineUsers(KeyValuePair<Pair, List<GroupFullInfoDto>> u)
            => ((u.Key.IsDirectlyPaired && _configService.Current.ShowSyncshellOfflineUsersSeparately)
                || !_configService.Current.ShowSyncshellOfflineUsersSeparately)
                && (!u.Key.IsOneSidedPair || u.Value.Any()) && !u.Key.IsOnline && !u.Key.UserPair.OwnPermissions.IsPaused();
        bool FilterOfflineSyncshellUsers(KeyValuePair<Pair, List<GroupFullInfoDto>> u)
            => (!u.Key.IsDirectlyPaired && !u.Key.IsOnline && !u.Key.UserPair.OwnPermissions.IsPaused());


        if (_configService.Current.ShowVisibleUsersSeparately)
        {
            var allVisiblePairs = ImmutablePairList(allPairs
                .Where(FilterVisibleUsers));
            var filteredVisiblePairs = BasicSortedDictionary(filteredPairs
                .Where(FilterVisibleUsers));

            drawFolders.Add(_drawEntityFactory.CreateDrawTagFolder(TagHandler.CustomVisibleTag, filteredVisiblePairs, allVisiblePairs));
        }

        List<IDrawFolder> groupFolders = new();
        foreach (var group in _pairManager.GroupPairs.Select(g => g.Key).OrderBy(g => g.GroupFullInfo.GroupAliasOrGID, StringComparer.OrdinalIgnoreCase))
        {
            var allGroupPairs = ImmutablePairList(allPairs
                .Where(u => FilterGroupUsers(u, group.GroupFullInfo)));

            var filteredGroupPairs = filteredPairs
                .Where(u => FilterGroupUsers(u, group.GroupFullInfo) && FilterOnlineOrPausedSelf(u))
                .OrderByDescending(u => u.Key.IsOnline)
                .ThenBy(u =>
                {
                    if (string.Equals(u.Key.UserData.UID, group.GroupFullInfo.OwnerUID, StringComparison.Ordinal)) return 0;
                    if (group.GroupFullInfo.GroupPairUserInfos.TryGetValue(u.Key.UserData.UID, out var info))
                    {
                        if (info.IsModerator()) return 1;
                        if (info.IsPinned()) return 2;
                    }
                    return u.Key.IsVisible ? 3 : 4;
                })
                .ThenBy(AlphabeticalSort, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(k => k.Key, k => k.Value);

            groupFolders.Add(_drawEntityFactory.CreateDrawGroupFolder(group, filteredGroupPairs, allGroupPairs));
        }

        if (_configService.Current.GroupUpSyncshells)
            drawFolders.Add(new DrawGroupedGroupFolder(groupFolders, _tagHandler, _uiSharedService));
        else
            drawFolders.AddRange(groupFolders);

        var tags = _tagHandler.GetAllTagsSorted();
        foreach (var tag in tags)
        {
            var allTagPairs = ImmutablePairList(allPairs
                .Where(u => FilterTagusers(u, tag)));
            var filteredTagPairs = BasicSortedDictionary(filteredPairs
                .Where(u => FilterTagusers(u, tag) && FilterOnlineOrPausedSelf(u)));

            drawFolders.Add(_drawEntityFactory.CreateDrawTagFolder(tag, filteredTagPairs, allTagPairs));
        }

        var allOnlineNotTaggedPairs = ImmutablePairList(allPairs
            .Where(FilterNotTaggedUsers));
        var onlineNotTaggedPairs = BasicSortedDictionary(filteredPairs
            .Where(u => FilterNotTaggedUsers(u) && FilterOnlineOrPausedSelf(u)));

        drawFolders.Add(_drawEntityFactory.CreateDrawTagFolder((_configService.Current.ShowOfflineUsersSeparately ? TagHandler.CustomOnlineTag : TagHandler.CustomAllTag), onlineNotTaggedPairs, allOnlineNotTaggedPairs));

        if (_configService.Current.ShowOfflineUsersSeparately)
        {
            var allOfflinePairs = ImmutablePairList(allPairs
                .Where(FilterOfflineUsers));
            var filteredOfflinePairs = BasicSortedDictionary(filteredPairs
                .Where(FilterOfflineUsers));

            drawFolders.Add(_drawEntityFactory.CreateDrawTagFolder(TagHandler.CustomOfflineTag, filteredOfflinePairs, allOfflinePairs));
            if (_configService.Current.ShowSyncshellOfflineUsersSeparately)
            {
                var allOfflineSyncshellUsers = ImmutablePairList(allPairs
                    .Where(FilterOfflineSyncshellUsers));
                var filteredOfflineSyncshellUsers = BasicSortedDictionary(filteredPairs
                    .Where(FilterOfflineSyncshellUsers));

                drawFolders.Add(_drawEntityFactory.CreateDrawTagFolder(TagHandler.CustomOfflineSyncshellTag,
                    filteredOfflineSyncshellUsers,
                    allOfflineSyncshellUsers));
            }
        }

        drawFolders.Add(_drawEntityFactory.CreateDrawTagFolder(TagHandler.CustomUnpairedTag,
            BasicSortedDictionary(filteredPairs.Where(u => u.Key.IsOneSidedPair)),
            ImmutablePairList(allPairs.Where(u => u.Key.IsOneSidedPair))));

        return drawFolders;
    }


    private string GetServerErrorByServer(int serverId)
    {
        var authFailureMessage = _apiController.GetAuthFailureMessageByServer(serverId);
        return GetServerErrorByState(_apiController.GetServerState(serverId), authFailureMessage);
    }

    private static string GetServerErrorByState(ServerState state, string? authFailureMessage)
    {
        return state switch
        {
            ServerState.Connecting => "Attempting to connect to the server.",
            ServerState.Reconnecting => "Connection to server interrupted, attempting to reconnect to the server.",
            ServerState.Disconnected => "You are currently disconnected from the Sinus Synchronous server.",
            ServerState.Disconnecting => "Disconnecting from the server",
            ServerState.Unauthorized => "Server Response: " + authFailureMessage,
            ServerState.Offline => "Your selected Sinuzs Synchronous server is currently offline.",
            ServerState.VersionMisMatch =>
                "Your plugin or the server you are connecting to is out of date. Please update your plugin now. If you already did so, contact the server provider to update their server to the latest version.",
            ServerState.RateLimited => "You are rate limited for (re)connecting too often. Disconnect, wait 10 minutes and try again.",
            ServerState.Connected => string.Empty,
            ServerState.NoSecretKey => "You have no secret key set for this current character. Open Settings -> Service Settings and set a secret key for the current character. You can reuse the same secret key for multiple characters.",
            ServerState.MultiChara => "Your Character Configuration has multiple characters configured with same name and world. You will not be able to connect until you fix this issue. Remove the duplicates from the configuration in Settings -> Service Settings -> Character Management and reconnect manually after.",
            ServerState.OAuthMisconfigured => "OAuth2 is enabled but not fully configured, verify in the Settings -> Service Settings that you have OAuth2 connected and, importantly, a UID assigned to your current character.",
            ServerState.OAuthLoginTokenStale => "Your OAuth2 login token is stale and cannot be used to renew. Go to the Settings -> Service Settings and unlink then relink your OAuth2 configuration.",
            ServerState.NoAutoLogon => "This character has automatic login into Sinus disabled. Press the connect button to connect to Sinus.",
            _ => string.Empty
        };
    }

    private Vector4 GetUidColorByServer(int serverId)
    {
        return GetUidColorByState(_apiController.GetServerState(serverId));
    }

    private static Vector4 GetUidColorByState(ServerState state)
    {
        return state switch
        {
            ServerState.Connecting => ImGuiColors.DalamudYellow,
            ServerState.Reconnecting => ImGuiColors.DalamudRed,
            ServerState.Connected => ImGuiColors.ParsedGreen,
            ServerState.Disconnected => ImGuiColors.DalamudYellow,
            ServerState.Disconnecting => ImGuiColors.DalamudYellow,
            ServerState.Unauthorized => ImGuiColors.DalamudRed,
            ServerState.VersionMisMatch => ImGuiColors.DalamudRed,
            ServerState.Offline => ImGuiColors.DalamudRed,
            ServerState.RateLimited => ImGuiColors.DalamudYellow,
            ServerState.NoSecretKey => ImGuiColors.DalamudYellow,
            ServerState.MultiChara => ImGuiColors.DalamudYellow,
            ServerState.OAuthMisconfigured => ImGuiColors.DalamudRed,
            ServerState.OAuthLoginTokenStale => ImGuiColors.DalamudRed,
            ServerState.NoAutoLogon => ImGuiColors.DalamudYellow,
            _ => ImGuiColors.DalamudRed
        };
    }

    private void CheckForCharacterAnalysis()
    {
        if (_hasUpdate)
        {
            _cachedAnalysis = _characterAnalyzer.LastAnalysis
                .ToDictionary(
                    kvp => (Dalamud.Game.ClientState.Objects.Enums.ObjectKind)kvp.Key,
                    kvp => kvp.Value
                );
            _hasUpdate = false;
        }
    }

    private void UiSharedService_GposeEnd()
    {
        IsOpen = _wasOpen;
    }

    private void UiSharedService_GposeStart()
    {
        _wasOpen = IsOpen;
        IsOpen = false;
    }
}