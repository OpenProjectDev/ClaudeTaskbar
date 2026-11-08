// The window's pieces, in the family's look: flat chips a step above what
// they sit on, sections on a title band, the app's own title bar.

import 'package:flutter/widgets.dart';

import 'package:claude_taskbar_settings/theme.dart';

const double kChipHeight = 34;
const double kChipRadius = 10;
const double kSectionRadius = 14;
const double kBandHeight = 40;
const double kSectionInset = 14;
const double kRowGap = 8;
const double kTitleHeight = 40;

/// A flat chip with a centred label. With [onTap] it answers the mouse: a
/// hand cursor, a step lighter under it; without, it only shows something.
class FlatChip extends StatefulWidget {
  const FlatChip({
    super.key,
    required this.label,
    required this.color,
    required this.textColor,
    this.onTap,
    this.height = kChipHeight,
    this.fontSize = 13,
  });

  final String label;
  final Color color, textColor;
  final VoidCallback? onTap;
  final double height, fontSize;

  @override
  State<FlatChip> createState() => _FlatChipState();
}

class _FlatChipState extends State<FlatChip> {
  bool _hover = false;

  @override
  Widget build(BuildContext context) {
    final active = widget.onTap != null;
    final chip = Container(
      height: widget.height,
      padding: const EdgeInsets.symmetric(horizontal: 10),
      alignment: Alignment.center,
      decoration: BoxDecoration(
        color: _hover && active ? lighter(widget.color) : widget.color,
        borderRadius: BorderRadius.circular(kChipRadius),
      ),
      child: Text(
        widget.label,
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
        textAlign: TextAlign.center,
        style: textStyle(widget.fontSize, FontWeight.w600, widget.textColor),
      ),
    );
    if (!active) return chip;
    return MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hover = true),
      onExit: (_) => setState(() => _hover = false),
      child: GestureDetector(
        behavior: HitTestBehavior.opaque,
        onTap: widget.onTap,
        child: chip,
      ),
    );
  }
}

/// A section: a box one step above the page, its title centred on a band one
/// step higher, its body [kSectionInset] in from every side. With [fill] it
/// takes the height it's given and centres its body in it.
class Section extends StatelessWidget {
  const Section({
    super.key,
    required this.title,
    required this.child,
    this.fill = false,
  });

  final String title;
  final Widget child;
  final bool fill;

  @override
  Widget build(BuildContext context) {
    final body = Padding(
      padding: const EdgeInsets.all(kSectionInset),
      child: child,
    );
    return Container(
      decoration: BoxDecoration(
        color: tone(1),
        borderRadius: BorderRadius.circular(kSectionRadius),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: fill ? MainAxisSize.max : MainAxisSize.min,
        children: [
          Container(
            height: kBandHeight,
            alignment: Alignment.center,
            decoration: BoxDecoration(
              color: tone(2),
              borderRadius: const BorderRadius.vertical(
                top: Radius.circular(kSectionRadius),
              ),
            ),
            child: Text(title, style: textStyle(13, FontWeight.w700, kInk)),
          ),
          if (fill) Expanded(child: Center(child: body)) else body,
        ],
      ),
    );
  }
}

enum Corner { close, back }

/// The title bar: [title] at the left, dragging the window; at the right the
/// close, always red, or on the licenses screen an orange back in its place.
class TitleBar extends StatelessWidget {
  const TitleBar({
    super.key,
    required this.title,
    required this.corner,
    required this.onCorner,
    required this.onDrag,
  });

  final Widget title;
  final Corner corner;
  final VoidCallback onCorner, onDrag;

  @override
  Widget build(BuildContext context) => Container(
    height: kTitleHeight,
    color: kTitleBar,
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Expanded(
          child: GestureDetector(
            behavior: HitTestBehavior.opaque,
            onPanStart: (_) => onDrag(),
            child: Padding(
              padding: const EdgeInsets.only(left: 16),
              child: Align(alignment: Alignment.centerLeft, child: title),
            ),
          ),
        ),
        _CornerButton(corner: corner, onTap: onCorner),
      ],
    ),
  );
}

class _CornerButton extends StatefulWidget {
  const _CornerButton({required this.corner, required this.onTap});
  final Corner corner;
  final VoidCallback onTap;

  @override
  State<_CornerButton> createState() => _CornerButtonState();
}

class _CornerButtonState extends State<_CornerButton> {
  bool _hover = false;

  @override
  Widget build(BuildContext context) {
    final fill = widget.corner == Corner.close ? kRed : kOrange;
    return Semantics(
      button: true,
      label: widget.corner == Corner.close ? 'Close' : 'Back',
      child: MouseRegion(
        onEnter: (_) => setState(() => _hover = true),
        onExit: (_) => setState(() => _hover = false),
        child: GestureDetector(
          behavior: HitTestBehavior.opaque,
          onTap: widget.onTap,
          child: Container(
            width: 46,
            color: _hover ? lighter(fill, .15) : fill,
            alignment: Alignment.center,
            child: CustomPaint(
              size: const Size.square(14),
              painter: _CornerGlyph(widget.corner),
            ),
          ),
        ),
      ),
    );
  }
}

/// The close x and the back arrow, painted: round line ends and joins.
class _CornerGlyph extends CustomPainter {
  const _CornerGlyph(this.corner);
  final Corner corner;

  @override
  void paint(Canvas canvas, Size size) {
    final c = size.center(Offset.zero);
    final pen = Paint()
      ..color = kWhite
      ..style = PaintingStyle.stroke
      ..strokeCap = StrokeCap.round
      ..strokeJoin = StrokeJoin.round
      ..isAntiAlias = true;
    if (corner == Corner.close) {
      const h = 6.0;
      pen.strokeWidth = 1.6;
      canvas.drawLine(c.translate(-h, -h), c.translate(h, h), pen);
      canvas.drawLine(c.translate(-h, h), c.translate(h, -h), pen);
    } else {
      pen.strokeWidth = 1.8;
      canvas.drawPath(
        Path()
          ..moveTo(c.dx - 1, c.dy - 6)
          ..lineTo(c.dx - 7, c.dy)
          ..lineTo(c.dx - 1, c.dy + 6)
          ..moveTo(c.dx - 7, c.dy)
          ..lineTo(c.dx + 7, c.dy),
        pen,
      );
    }
  }

  @override
  bool shouldRepaint(_CornerGlyph old) => old.corner != corner;
}
