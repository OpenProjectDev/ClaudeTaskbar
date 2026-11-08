// The circles app, ClaudeTaskbar.exe, does everything that touches the
// system: the Run value, the Claude Code hooks, ending itself. This window
// asks it through its switches (src/ClaudeTaskbar/Cli.cs), each of which
// prints one line of JSON and exits 0, or prints {"failed": "..."} and exits 1.

import 'dart:convert';
import 'dart:io';

class HostError implements Exception {
  const HostError(this.message);
  final String message;

  @override
  String toString() => message;
}

enum HookState { notInstalled, partial, outdated, installed }

class HookStatus {
  const HookStatus({
    required this.state,
    required this.entries,
    required this.total,
    required this.script,
    required this.error,
  });

  factory HookStatus.fromJson(Map<String, dynamic> j) => HookStatus(
    state: switch (j['state']) {
      'Installed' => HookState.installed,
      'Outdated' => HookState.outdated,
      'Partial' => HookState.partial,
      _ => HookState.notInstalled,
    },
    entries: j['entries'] as int,
    total: j['total'] as int,
    script: j['script'] as bool,
    error: j['error'] as String?,
  );

  final HookState state;
  final int entries, total;
  final bool script;

  /// Set when settings.json can't be read.
  final String? error;
}

class HostStatus {
  const HostStatus({
    required this.version,
    required this.autostart,
    required this.hooks,
  });

  factory HostStatus.fromJson(Map<String, dynamic> j) => HostStatus(
    version: j['version'] as String,
    autostart: j['autostart'] as bool,
    hooks: HookStatus.fromJson(j['hooks'] as Map<String, dynamic>),
  );

  final String version;
  final bool autostart;
  final HookStatus hooks;
}

class Host {
  const Host(this.exe);

  /// The exe given with `--host <path>` (the circles app passes its own
  /// path), else the one this app is deployed beside: it lives in the
  /// settings folder next to ClaudeTaskbar.exe.
  factory Host.locate(List<String> args) {
    final i = args.indexOf('--host');
    if (i >= 0 && i + 1 < args.length) return Host(args[i + 1]);
    final here = File(Platform.resolvedExecutable).parent.parent;
    return Host('${here.path}${Platform.pathSeparator}ClaudeTaskbar.exe');
  }

  final String exe;

  Future<HostStatus> status() async =>
      HostStatus.fromJson(await _run(['--status']));

  Future<HostStatus> setAutostart(bool on) async =>
      HostStatus.fromJson(await _run(['--autostart', on ? 'on' : 'off']));

  Future<HostStatus> installHooks() async =>
      HostStatus.fromJson(await _run(['--hooks', 'install']));

  Future<HostStatus> removeHooks() async =>
      HostStatus.fromJson(await _run(['--hooks', 'uninstall']));

  /// Returns once the circles app's process has ended (it waits on the
  /// process itself), or right away when it isn't running.
  Future<void> quit() => _run(['--quit']);

  Future<Map<String, dynamic>> _run(List<String> args) async {
    if (!File(exe).existsSync()) throw HostError('$exe is missing');
    final ProcessResult r;
    try {
      r = await Process.run(exe, args, stdoutEncoding: utf8);
    } on ProcessException catch (e) {
      throw HostError(e.message);
    }
    final lines = (r.stdout as String)
        .split('\n')
        .map((l) => l.trim())
        .where((l) => l.startsWith('{'));
    if (lines.isEmpty) {
      throw HostError('ClaudeTaskbar.exe gave no answer, exit code ${r.exitCode}');
    }
    final json = jsonDecode(lines.last) as Map<String, dynamic>;
    if (r.exitCode != 0 || json['failed'] != null) {
      throw HostError(json['failed'] as String? ?? 'exit code ${r.exitCode}');
    }
    return json;
  }
}
