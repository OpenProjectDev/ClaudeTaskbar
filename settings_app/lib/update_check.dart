// The version chip's check: GitHub's REST API for the latest release
// (documented: GET /repos/{owner}/{repo}/releases/latest, 404 when there is
// none; a User-Agent is required). No artificial wait: the chip says
// "Checking" for as long as the request really takes.

import 'dart:async';
import 'dart:convert';
import 'dart:io';

/// Where releases are published. The public repository's name is the
/// developer's to choose; this is the expected one.
const String kGitHubRepo = 'OpenProjectDev/ClaudeTaskbar';

sealed class UpdateResult {
  const UpdateResult();
}

class NoRelease extends UpdateResult {
  const NoRelease();
}

class LatestRelease extends UpdateResult {
  const LatestRelease(this.version, this.url);
  final String version;
  final String? url;
}

Future<UpdateResult> checkLatestRelease(String? currentVersion) async {
  final client = HttpClient()..connectionTimeout = const Duration(seconds: 10);
  try {
    final request = await client.getUrl(
      Uri.parse('https://api.github.com/repos/$kGitHubRepo/releases/latest'),
    );
    request.headers
      ..set(HttpHeaders.userAgentHeader, 'ClaudeTaskbar/${currentVersion ?? 'unknown'}')
      ..set(HttpHeaders.acceptHeader, 'application/vnd.github+json');
    final response = await request.close().timeout(const Duration(seconds: 10));
    final body = await response.transform(utf8.decoder).join();
    if (response.statusCode == HttpStatus.notFound) return const NoRelease();
    if (response.statusCode != HttpStatus.ok) {
      throw HttpException('GitHub answered ${response.statusCode}');
    }
    final json = jsonDecode(body) as Map<String, dynamic>;
    final tag = (json['tag_name'] as String? ?? '').replaceFirst(
      RegExp('^[vV]'),
      '',
    );
    return LatestRelease(tag, json['html_url'] as String?);
  } finally {
    client.close(force: true);
  }
}

/// Whether [latest] is newer than [current], both "x.y.z"; null when either
/// can't be read.
bool? isNewer(String latest, String? current) {
  List<int>? parse(String? v) {
    final parts = v?.split('.').map(int.tryParse).toList();
    if (parts == null || parts.isEmpty || parts.contains(null)) return null;
    return parts.cast<int>();
  }

  final a = parse(latest), b = parse(current);
  if (a == null || b == null) return null;
  for (var i = 0; i < a.length || i < b.length; i++) {
    final x = i < a.length ? a[i] : 0, y = i < b.length ? b[i] : 0;
    if (x != y) return x > y;
  }
  return false;
}
