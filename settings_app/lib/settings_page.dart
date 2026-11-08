// The app's only window besides the circles: autostart, the Claude Code
// hooks, About, what the colours mean, the version with an update check, the
// licenses, and terminating the app. Two columns that end level: About's box
// stretches on the left, Terminate sits on the common foot on the right.
//
// The family's rules: tones instead of lines, 34-tall flat chips a step above
// what they sit on, actions that can't be taken back armed first (yellow,
// then a second click), a dimmed chip that still answers and says why,
// messages in place rather than in a dialog, short labels with no full stop
// or colon at a line's end, and nothing pleading.

import 'dart:async';
import 'dart:io';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:window_manager/window_manager.dart';

import 'package:claude_taskbar_settings/host.dart';
import 'package:claude_taskbar_settings/licenses.dart';
import 'package:claude_taskbar_settings/theme.dart';
import 'package:claude_taskbar_settings/ui/family_marks.dart';
import 'package:claude_taskbar_settings/ui/widgets.dart';
import 'package:claude_taskbar_settings/update_check.dart';

const double kWindowWidth = 700;
const double _leftWidth = 300;
const double _columnGap = 12;
const double _pageInset = 16;
const double _sectionGap = 12;
const Duration _armFor = Duration(seconds: 3);

/// Sizes the window to [size] and holds it there: the minimum and maximum
/// both [size], since the window keeps its resize frame (see main.dart).
Future<void> pinSize(Size size) async {
  await windowManager.setMinimumSize(Size.zero);
  await windowManager.setMaximumSize(const Size(100000, 100000));
  await windowManager.setSize(size);
  await windowManager.setMinimumSize(size);
  await windowManager.setMaximumSize(size);
}

class SettingsPage extends StatefulWidget {
  const SettingsPage({
    super.key,
    required this.host,
    required this.status,
    required this.statusError,
    required this.onFirstLayout,
  });

  final Host host;
  final HostStatus? status;
  final String? statusError;

  /// Called once with the page's natural height, before the window shows.
  final Future<void> Function(double height) onFirstLayout;

  @override
  State<SettingsPage> createState() => _SettingsPageState();
}

enum _Update { idle, checking, upToDate, available, latestUnknown, noRelease, failed }

class _SettingsPageState extends State<SettingsPage> with WindowListener {
  final _contentKey = GlobalKey();
  HostStatus? _status;
  String? _notice;
  bool _noticeIsError = false;
  String? _armed;
  Timer? _armTimer;
  bool _busy = false;
  bool _shown = false;
  double? _height;

  bool _licensesOpen = false;
  List<LicenseItem>? _licenses;

  _Update _update = _Update.idle;
  String? _latestVersion, _latestUrl;

  @override
  void initState() {
    super.initState();
    _status = widget.status;
    if (widget.statusError != null) {
      _say("Claude Taskbar couldn't be asked: ${widget.statusError}", error: true);
    }
    windowManager.addListener(this);
    WidgetsBinding.instance.addPostFrameCallback((_) => _fit());
  }

  @override
  void dispose() {
    windowManager.removeListener(this);
    _armTimer?.cancel();
    super.dispose();
  }

  /// The window takes the page's natural height: shown the first time, and
  /// again whenever a notice changes it. The licenses screen keeps the size.
  Future<void> _fit() async {
    if (_licensesOpen) return;
    final box = _contentKey.currentContext?.findRenderObject() as RenderBox?;
    if (box == null || !box.hasSize) return;
    final h = box.size.height;
    if (h == _height) return;
    _height = h;
    if (!_shown) {
      _shown = true;
      await widget.onFirstLayout(h);
    } else {
      await pinSize(Size(kWindowWidth, h));
    }
  }

  @override
  void onWindowBlur() => _disarm();

  // ---- Actions.

  Future<void> _close() async {
    await windowManager.hide();
    exit(0);
  }

  void _escape() => _licensesOpen ? _closeLicenses() : _close();

  Future<void> _openLicenses() async {
    _disarm();
    setState(() => _licensesOpen = true);
    final items = _licenses ?? await loadLicenses();
    if (mounted) setState(() => _licenses = items);
  }

  void _closeLicenses() {
    setState(() => _licensesOpen = false);
    WidgetsBinding.instance.addPostFrameCallback((_) => _fit());
  }

  /// Runs a call to the circles app, then shows what it reports; a failure
  /// becomes a red notice under the hooks.
  Future<void> _call(Future<HostStatus> Function() call, String failure) async {
    if (_busy) return;
    _busy = true;
    try {
      final status = await call();
      setState(() {
        _status = status;
        _notice = null;
      });
    } catch (e) {
      _say('$failure: $e', error: true);
    } finally {
      _busy = false;
      WidgetsBinding.instance.addPostFrameCallback((_) => _fit());
    }
  }

  void _toggleAutostart() {
    final s = _status;
    if (s == null) return _say('Autostart is unknown: Claude Taskbar did not answer', error: true);
    _call(() => widget.host.setAutostart(!s.autostart), "Couldn't change autostart");
  }

  void _install() {
    final h = _status?.hooks;
    if (h == null) return _say('The hooks are unknown: Claude Taskbar did not answer', error: true);
    if (h.error != null) return _say(h.error!, error: true);
    if (h.state == HookState.installed) return _say('Nothing to install, the hooks are up to date');
    _call(widget.host.installHooks, "Couldn't install the hooks");
  }

  void _remove() {
    final h = _status?.hooks;
    if (h == null) return _say('The hooks are unknown: Claude Taskbar did not answer', error: true);
    if (h.error != null) return _say(h.error!, error: true);
    if (h.state == HookState.notInstalled) return _say('Nothing to remove');
    if (_armed != 'remove') return _arm('remove');
    _disarm();
    _call(widget.host.removeHooks, "Couldn't remove the hooks");
  }

  Future<void> _terminate() async {
    if (_armed != 'terminate') return _arm('terminate');
    if (_busy) return;
    _busy = true;
    setState(() => _armed = 'terminating');
    try {
      await widget.host.quit();
      await _close();
    } catch (e) {
      _busy = false;
      setState(() => _armed = null);
      _say("Couldn't terminate Claude Taskbar: $e", error: true);
    }
  }

  void _versionClicked() {
    switch (_update) {
      case _Update.checking:
        return;
      case _Update.available || _Update.latestUnknown when _latestUrl != null:
        unawaited(Process.run('explorer.exe', [_latestUrl!]));
      default:
        unawaited(_checkForUpdates());
    }
  }

  Future<void> _checkForUpdates() async {
    setState(() => _update = _Update.checking);
    final current = _status?.version;
    try {
      final result = await checkLatestRelease(current);
      setState(() {
        switch (result) {
          case NoRelease():
            _update = _Update.noRelease;
          case LatestRelease(:final version, :final url):
            _latestVersion = version;
            _latestUrl = url;
            _update = switch (isNewer(version, current)) {
              true => _Update.available,
              false => _Update.upToDate,
              null => _Update.latestUnknown,
            };
        }
      });
    } catch (_) {
      setState(() => _update = _Update.failed);
    }
  }

  void _say(String text, {bool error = false}) {
    setState(() {
      _notice = text;
      _noticeIsError = error;
    });
    WidgetsBinding.instance.addPostFrameCallback((_) => _fit());
  }

  void _arm(String id) {
    _armTimer?.cancel();
    _armTimer = Timer(_armFor, _disarm);
    setState(() => _armed = id);
  }

  void _disarm() {
    _armTimer?.cancel();
    if (_armed == null || _armed == 'terminating') return;
    setState(() => _armed = null);
  }

  /// A chip's click: one elsewhere lets an armed chip go.
  VoidCallback _tap(String id, VoidCallback action) => () {
    if (_armed != id) _disarm();
    action();
  };

  // ---- What the chips say.

  (String, Color) get _hookLook {
    final h = _status?.hooks;
    if (h == null) return ('Unknown', tone(2));
    if (h.error != null) return ("settings.json can't be read", kRed);
    return switch (h.state) {
      HookState.installed => ('Installed', kGreen),
      HookState.outdated => ('An older hook script is installed', kOrange),
      HookState.partial => (
        '${h.total - h.entries} of ${h.total} entries missing'
            '${h.script ? '' : ', script missing'}',
        kOrange,
      ),
      HookState.notInstalled => ('Not installed', kRed),
    };
  }

  String get _installLabel => switch (_status?.hooks.state) {
    HookState.outdated => 'Update',
    HookState.partial => 'Repair',
    _ => 'Install',
  };

  (String, Color) get _versionLook => switch (_update) {
    _Update.checking => ('Checking', tone(1)),
    _Update.upToDate => ('Up to date', kGreen),
    _Update.available => ('$_latestVersion available', kBlue),
    _Update.latestUnknown => ('Latest is $_latestVersion', kBlue),
    _Update.noRelease => ('No release yet', tone(1)),
    _Update.failed => ("Couldn't reach GitHub", kRed),
    _Update.idle => ('Version ${_status?.version ?? 'unknown'}', tone(1)),
  };

  static const _legend = [
    (kCircleOrange, 'Working, Claude or a subagent'),
    (kCircleYellow, 'Turn done, background commands running'),
    (kCirclePurple, 'Waiting for your answer or permission'),
    (kCircleRed, 'Stopped with an error'),
    (kCircleGreen, 'Finished in the last 10 minutes'),
    (kCircleBlue, 'Finished longer ago'),
    (kCircleGrey, 'Empty desktop, or marked seen'),
  ];

  static const _mouse = [
    ('Click', 'Open the session'),
    ('Double-click', 'Mark a finished one seen, or undo'),
    ('Right-click', 'Open this window'),
  ];

  // ---- Layout.

  @override
  Widget build(BuildContext context) {
    final page = _licensesOpen
        ? Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              TitleBar(
                title: Text('Licenses', style: textStyle(15, FontWeight.w600, kInk)),
                corner: Corner.back,
                onCorner: _closeLicenses,
                onDrag: windowManager.startDragging,
              ),
              Expanded(child: LicensesView(items: _licenses)),
            ],
          )
        // Laid out at its natural height, whatever the window's: that height
        // is what the window is sized to (_fit).
        : OverflowBox(
            alignment: Alignment.topCenter,
            minHeight: 0,
            maxHeight: double.infinity,
            child: Column(
              key: _contentKey,
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                TitleBar(
                  title: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Text('Claude', style: textStyle(15, FontWeight.w600, kInk)),
                      const SizedBox(width: 5),
                      Text('Taskbar', style: textStyle(15, FontWeight.w600, kBlue)),
                    ],
                  ),
                  corner: Corner.close,
                  onCorner: _close,
                  onDrag: windowManager.startDragging,
                ),
                Padding(
                  padding: const EdgeInsets.all(_pageInset),
                  child: IntrinsicHeight(
                    child: Row(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        SizedBox(width: _leftWidth, child: _leftColumn()),
                        const SizedBox(width: _columnGap),
                        Expanded(child: _rightColumn()),
                      ],
                    ),
                  ),
                ),
              ],
            ),
          );

    return Focus(
      autofocus: true,
      onKeyEvent: (_, event) {
        if (event is KeyDownEvent && event.logicalKey == LogicalKeyboardKey.escape) {
          _escape();
          return KeyEventResult.handled;
        }
        return KeyEventResult.ignored;
      },
      // The whole window drags from anywhere that isn't a control, and a click
      // there lets an armed chip go.
      child: GestureDetector(
        behavior: HitTestBehavior.opaque,
        // onTap, not onTapDown: it fires only when no chip took the click, so
        // a slow second click on an armed chip still confirms it.
        onTap: _disarm,
        onPanStart: (_) => windowManager.startDragging(),
        child: ColoredBox(color: kPage, child: page),
      ),
    );
  }

  Widget _leftColumn() {
    final (hookText, hookColor) = _hookLook;
    final hooks = _status?.hooks;
    final canInstall = hooks != null && hooks.error == null && hooks.state != HookState.installed;
    final canRemove = hooks != null && hooks.error == null && hooks.state != HookState.notInstalled;
    final removeArmed = _armed == 'remove';
    final autostart = _status?.autostart ?? false;
    final autostartBg = autostart ? kBlue : tone(2);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Section(
          title: 'Startup',
          child: FlatChip(
            label: 'Start with Windows',
            color: autostartBg,
            textColor: _status == null ? ink(.4) : onColor(autostartBg),
            onTap: _tap('autostart', _toggleAutostart),
          ),
        ),
        const SizedBox(height: _sectionGap),
        Section(
          title: 'Claude Code hooks',
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              FlatChip(label: hookText, color: hookColor, textColor: onColor(hookColor)),
              const SizedBox(height: kRowGap),
              Row(
                children: [
                  Expanded(
                    child: FlatChip(
                      label: _installLabel,
                      color: canInstall ? kBlue : tone(2),
                      textColor: canInstall ? onColor(kBlue) : ink(.4),
                      onTap: _tap('install', _install),
                    ),
                  ),
                  const SizedBox(width: kRowGap),
                  Expanded(
                    child: FlatChip(
                      label: removeArmed ? 'Click again' : 'Remove',
                      color: removeArmed ? kArmedYellow : tone(2),
                      textColor: removeArmed
                          ? onColor(kArmedYellow)
                          : canRemove
                          ? kInk
                          : ink(.4),
                      onTap: _tap('remove', _remove),
                    ),
                  ),
                ],
              ),
              if (_notice != null) ...[
                const SizedBox(height: kRowGap),
                Text(
                  _notice!,
                  style: textStyle(12, FontWeight.w500, _noticeIsError ? kRed : ink(.75)),
                ),
              ],
            ],
          ),
        ),
        const SizedBox(height: _sectionGap),
        // About, as the OpenProject apps show it: the family's three
        // marks across the row as equal squares (this app's, OpenProject's in
        // the middle, OpenDesign's) and the family line; nothing more.
        Expanded(
          child: Section(
            title: 'About',
            fill: true,
            child: Builder(
              builder: (context) {
                // Three equal squares across the section's inner width. Not
                // a LayoutBuilder: the columns are sized by IntrinsicHeight,
                // which a LayoutBuilder can't answer.
                const side = (_leftWidth - 2 * kSectionInset - 2 * kRowGap) / 3;
                return Column(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Row(
                      children: [
                        ClipRRect(
                          borderRadius: BorderRadius.circular(side * 225 / 1024),
                          child: Image.asset(
                            'assets/claudetaskbar-mark.png',
                            width: side,
                            height: side,
                            filterQuality: FilterQuality.medium,
                          ),
                        ),
                        const SizedBox(width: kRowGap),
                        FamilyMarkView(FamilyMark.openProject, size: side),
                        const SizedBox(width: kRowGap),
                        FamilyMarkView(FamilyMark.openDesign, size: side),
                      ],
                    ),
                    const SizedBox(height: 10),
                    Text(
                      'An OpenProject app',
                      textAlign: TextAlign.center,
                      style: textStyle(12, FontWeight.w500, ink(.75)),
                    ),
                  ],
                );
              },
            ),
          ),
        ),
      ],
    );
  }

  Widget _rightColumn() {
    final (versionText, versionBg) = _versionLook;
    final terminateArmed = _armed == 'terminate' || _armed == 'terminating';
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Section(
          title: 'Colours',
          child: Column(
            children: [
              for (final (color, meaning) in _legend)
                SizedBox(
                  height: 25,
                  child: Row(
                    children: [
                      Container(
                        width: 12,
                        height: 12,
                        decoration: BoxDecoration(color: color, shape: BoxShape.circle),
                      ),
                      const SizedBox(width: 10),
                      Text(meaning, style: textStyle(13, FontWeight.w500, kInk)),
                    ],
                  ),
                ),
            ],
          ),
        ),
        const SizedBox(height: _sectionGap),
        Section(
          title: 'Mouse',
          child: Column(
            children: [
              for (final (i, (gesture, effect)) in _mouse.indexed) ...[
                if (i > 0) const SizedBox(height: 6),
                Row(
                  children: [
                    SizedBox(
                      width: 100,
                      child: FlatChip(
                        label: gesture,
                        color: tone(2),
                        textColor: kInk,
                        height: 28,
                        fontSize: 12,
                      ),
                    ),
                    const SizedBox(width: 12),
                    Text(effect, style: textStyle(13, FontWeight.w500, kInk)),
                  ],
                ),
              ],
            ],
          ),
        ),
        const SizedBox(height: _sectionGap),
        const Spacer(),
        // The version (a click checks GitHub for a newer release) and the
        // licenses, then Terminate on the foot.
        Row(
          children: [
            Expanded(
              child: FlatChip(
                label: versionText,
                color: versionBg,
                textColor: versionBg == tone(1) ? kInk : onColor(versionBg),
                onTap: _tap('version', _versionClicked),
              ),
            ),
            const SizedBox(width: kRowGap),
            Expanded(
              child: FlatChip(
                label: 'Licenses',
                color: tone(1),
                textColor: kInk,
                onTap: _tap('licenses', _openLicenses),
              ),
            ),
          ],
        ),
        const SizedBox(height: kRowGap),
        // Red with white text, and armed first: it stops the app.
        FlatChip(
          label: _armed == 'terminating'
              ? 'Terminating'
              : terminateArmed
              ? 'Click again to terminate'
              : 'Terminate Claude Taskbar',
          color: terminateArmed ? kArmedYellow : kRed,
          textColor: terminateArmed ? onColor(kArmedYellow) : kWhite,
          onTap: _tap('terminate', _terminate),
        ),
      ],
    );
  }
}
