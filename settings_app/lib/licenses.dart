// The Licenses screen: everything Claude Taskbar ships, each with its full
// text. First this app (GPLv3 or later, the repository's LICENSE), the two
// family marks and the font; then what the circles app bundles (the .NET
// runtimes, since the release build is self-contained, and every package in
// its .deps.json); then Flutter and
// the packages this window is built from, as Flutter's own license registry
// lists them. Copyright lines are the packages' own (their .nuspec files);
// the texts are from assets/licenses/ and assets/fonts/manrope/OFL.txt, each
// copied from its source: the MIT text from the System.Drawing.Common
// package, Apache-2.0 from apache.org, SQLite's statement from
// sqlite.org/copyright.html, the Flaticon terms from the license
// certificates.

import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';

import 'package:claude_taskbar_settings/theme.dart';
import 'package:claude_taskbar_settings/ui/widgets.dart';

class LicenseItem {
  const LicenseItem(this.name, this.what, this.copyright, this.license, this.text);
  final String name, what, copyright, license, text;
}

Future<List<LicenseItem>> loadLicenses() async {
  Future<String> asset(String path) async =>
      (await rootBundle.loadString(path)).replaceAll('\r\n', '\n').trim();
  final mit = await asset('assets/licenses/MIT-dotnet.txt');
  final apache = await asset('assets/licenses/Apache-2.0.txt');
  final flaticon = await asset('assets/licenses/Flaticon.txt');
  final items = [
    LicenseItem(
      'Claude Taskbar',
      'This app, its icon included',
      'Copyright (C) 2026 Hosein Sadeqi',
      'GNU General Public License v3.0 or later',
      await asset('assets/licenses/GPL-3.0.txt'),
    ),
    LicenseItem(
      'OpenProject mark',
      'Ai brain icon, Flaticon ID 12350685',
      'Designed by gravisio from Flaticon',
      'Flaticon free license, with attribution',
      flaticon.replaceAll('{author}', 'gravisio'),
    ),
    LicenseItem(
      'OpenDesign mark',
      'Ruler and pencil icon, Flaticon ID 10573683',
      'Designed by Arkinasi from Flaticon',
      'Flaticon free license, with attribution',
      flaticon.replaceAll('{author}', 'Arkinasi'),
    ),
    LicenseItem(
      'Manrope',
      'The font of this window',
      'Copyright 2018 The Manrope Project Authors',
      'SIL Open Font License 1.1',
      await asset('assets/fonts/manrope/OFL.txt'),
    ),
    LicenseItem(
      '.NET 8 runtime and Windows Desktop runtime',
      'Built into ClaudeTaskbar.exe. Their third-party notices are in the licenses folder beside it',
      '© Microsoft Corporation. All rights reserved.',
      'MIT',
      mit,
    ),
    const LicenseItem(
      'Microsoft Visual C++ runtime',
      'msvcp140.dll, vcruntime140.dll and vcruntime140_1.dll beside this window',
      '© Microsoft Corporation. All rights reserved.',
      'Visual Studio redistributable code',
      'Redistributed with this app under the Visual Studio license terms, which allow the files in its redistributable folder to be distributed with an application.',
    ),
    LicenseItem(
      'Microsoft.Data.Sqlite 8.0.10',
      "Reads VS Code's tab lists, with Microsoft.Data.Sqlite.Core",
      '© Microsoft Corporation. All rights reserved.',
      'MIT',
      mit,
    ),
    LicenseItem(
      'SQLitePCLRaw 2.1.6',
      'core, provider.e_sqlite3, lib.e_sqlite3 and bundle_e_sqlite3',
      'Copyright 2014-2023 SourceGear, LLC',
      'Apache License 2.0',
      apache,
    ),
    LicenseItem(
      'SQLite',
      'The e_sqlite3 library inside SQLitePCLRaw',
      '',
      'Public domain',
      await asset('assets/licenses/SQLite.txt'),
    ),
    LicenseItem(
      'System.Drawing.Common 8.0.10',
      'Drawing the circles',
      '© Microsoft Corporation. All rights reserved.',
      'MIT',
      mit,
    ),
  ];

  // Flutter's registry: one entry may cover several packages, and a package
  // may have several entries. Grouped by package, in the registry's order.
  final byPackage = <String, List<String>>{};
  await for (final entry in LicenseRegistry.licenses) {
    final text = entry.paragraphs
        .map((p) => p.indent == LicenseParagraph.centeredIndent
            ? p.text
            : '${'  ' * p.indent}${p.text}')
        .join('\n\n');
    for (final package in entry.packages) {
      (byPackage[package] ??= []).add(text);
    }
  }
  for (final MapEntry(key: package, value: texts) in byPackage.entries) {
    items.add(LicenseItem(
      package,
      'Part of Flutter or a package this window is built with',
      '',
      '',
      texts.join('\n\n────────\n\n'),
    ));
  }
  return items;
}

/// The list, under the title bar, scrolled with the wheel or the slim bar.
class LicensesView extends StatefulWidget {
  const LicensesView({super.key, required this.items});
  final List<LicenseItem>? items;

  @override
  State<LicensesView> createState() => _LicensesViewState();
}

class _LicensesViewState extends State<LicensesView> {
  final _scroll = ScrollController();

  @override
  void dispose() {
    _scroll.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final items = widget.items;
    if (items == null) return const SizedBox.expand();
    return RawScrollbar(
      controller: _scroll,
      thumbVisibility: true,
      interactive: true,
      thickness: 6,
      radius: const Radius.circular(3),
      thumbColor: ink(.22),
      mainAxisMargin: 8,
      crossAxisMargin: 6,
      child: ScrollConfiguration(
        behavior: const ScrollBehavior().copyWith(
          scrollbars: false,
          overscroll: false,
          physics: const ClampingScrollPhysics(),
        ),
        child: ListView.builder(
          controller: _scroll,
          padding: const EdgeInsets.fromLTRB(20, 16, 30, 0),
          itemCount: items.length,
          itemBuilder: (context, i) => _Item(items[i]),
        ),
      ),
    );
  }
}

class _Item extends StatelessWidget {
  const _Item(this.item);
  final LicenseItem item;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(bottom: 28),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(item.name, style: textStyle(15, FontWeight.w700, kInk)),
        const SizedBox(height: 3),
        Text(item.what, style: textStyle(13, FontWeight.w500, ink(.75))),
        if (item.copyright.isNotEmpty) ...[
          const SizedBox(height: 2),
          Text(item.copyright, style: textStyle(13, FontWeight.w500, ink(.75))),
        ],
        if (item.license.isNotEmpty) ...[
          const SizedBox(height: 2),
          Text(item.license, style: textStyle(13, FontWeight.w600, kBlue)),
        ],
        const SizedBox(height: 10),
        Container(
          padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
          decoration: BoxDecoration(
            color: tone(1),
            borderRadius: BorderRadius.circular(kChipRadius),
          ),
          child: Text(item.text, style: textStyle(12, FontWeight.w500, ink(.72))),
        ),
      ],
    ),
  );
}
