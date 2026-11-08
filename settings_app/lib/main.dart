// Claude Taskbar's settings window, a Flutter app of its own beside the
// circles app (src/ClaudeTaskbar), which starts it on a right-click on a
// circle, on a second launch of ClaudeTaskbar.exe, and at its own start when
// the Claude Code hooks aren't installed. Everything it changes goes through
// that app's switches (lib/host.dart). Flutter because its text is crisp
// where the earlier GDI+ and WPF windows' text was not.

import 'package:flutter/widgets.dart';
import 'package:window_manager/window_manager.dart';

import 'package:claude_taskbar_settings/host.dart';
import 'package:claude_taskbar_settings/settings_page.dart';
import 'package:claude_taskbar_settings/theme.dart';

Future<void> main(List<String> args) async {
  WidgetsFlutterBinding.ensureInitialized();
  await windowManager.ensureInitialized();

  // The state is read before anything is drawn, so no chip first shows a
  // state it doesn't have.
  final host = Host.locate(args);
  HostStatus? status;
  String? statusError;
  try {
    status = await host.status();
  } on HostError catch (e) {
    statusError = e.message;
  } catch (e) {
    // An answer this version can't read (the exe and the settings folder from
    // different versions, say): the window still opens and says so, rather
    // than staying hidden while the process runs.
    statusError = 'unexpected answer: $e';
  }

  // Hidden until the page has measured itself (see the runner's
  // flutter_window.cpp): then sized, centred and shown.
  await windowManager.waitUntilReadyToShow(
    const WindowOptions(
      size: Size(kWindowWidth, 560),
      center: true,
      backgroundColor: kPage,
      titleBarStyle: TitleBarStyle.hidden,
      windowButtonVisibility: false,
      title: 'Claude Taskbar',
    ),
  );
  // Frameless: the window is all client area, the page edge to edge, with
  // the system shadow kept. It stays "resizable" as far as Windows knows
  // (WS_THICKFRAME): without that frame Windows 11 draws the corners square
  // even when asked for round ones (the runner asks), as setResizable(false)
  // showed. Its size is pinned instead (SettingsPage._fit sets the minimum
  // and maximum to the page's size), and the Flutter view covers every edge,
  // so there is nothing to drag a size from.
  await windowManager.setAsFrameless();
  await windowManager.setHasShadow(true);
  await windowManager.setMaximizable(false);

  runApp(
    WidgetsApp(
      color: kBlue,
      title: 'Claude Taskbar',
      debugShowCheckedModeBanner: false,
      textStyle: textStyle(13, FontWeight.w500, kInk),
      pageRouteBuilder: <T>(RouteSettings settings, WidgetBuilder builder) =>
          PageRouteBuilder<T>(
            settings: settings,
            pageBuilder: (context, _, _) => builder(context),
          ),
      home: SettingsPage(
        host: host,
        status: status,
        statusError: statusError,
        onFirstLayout: (height) async {
          await pinSize(Size(kWindowWidth, height));
          await windowManager.center();
          await windowManager.show();
          // Windows may refuse this process foreground rights (it goes by
          // who received the last input: a click on a circle goes to
          // Explorer's taskbar), and then the window would open behind
          // whatever is in front. Briefly topmost raises it without them.
          await windowManager.setAlwaysOnTop(true);
          await windowManager.setAlwaysOnTop(false);
          await windowManager.focus();
        },
      ),
    ),
  );
}
