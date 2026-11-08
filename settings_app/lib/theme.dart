// The OpenProject family's look: a dark page, tones instead of lines, one
// blue, one red.

import 'dart:math' as math;

import 'package:flutter/widgets.dart';

const Color kPage = Color(0xFF1E1F21);
const Color kTitleBar = Color(0xFF1A1B1D);
const Color kLevel1 = Color(0xFF26282C);
const Color kInk = Color(0xFFE3E2E6);
const Color kBlue = Color(0xFF2196F3);

/// The one red, always with white on it.
const Color kRed = Color(0xFFE53935);
const Color kGreen = Color(0xFF2E7D32);
const Color kOrange = Color(0xFFEF6C00);

/// A control waiting for its second click: strong yellow, dark type.
const Color kArmedYellow = Color(0xFFFFD60A);

const Color kWhite = Color(0xFFFFFFFF);
const Color kBlack = Color(0xFF000000);

/// The tone ladder: 0 the page, 1 a tile on it, each level above one clear
/// step lighter (6% of the text colour per step
/// over level 1).
Color tone(double level) => level <= 0
    ? kPage
    : level <= 1
    ? Color.lerp(kPage, kLevel1, level)!
    : Color.lerp(kLevel1, kInk, math.min(1, .06 * (level - 1)))!;

/// Text on a coloured background goes by the colour: black above luminance
/// 0.53, else white.
Color onColor(Color bg) => bg.computeLuminance() > .53 ? kBlack : kWhite;

/// The text colour at [alpha], for text that steps back.
Color ink(double alpha) => kInk.withValues(alpha: alpha);

/// A step lighter: a surface under the mouse.
Color lighter(Color c, [double t = .12]) => Color.lerp(c, kWhite, t)!;

TextStyle textStyle(double size, FontWeight weight, Color color) => TextStyle(
  fontFamily: 'Manrope',
  fontSize: size,
  fontWeight: weight,
  color: color,
  decoration: TextDecoration.none,
  // Leading split evenly above and below, so a line sits in the middle of
  // its box.
  leadingDistribution: TextLeadingDistribution.even,
);

/// The circles' colours, exactly as the taskbar draws them.
const Color kCircleOrange = Color.fromARGB(255, 249, 115, 22);
const Color kCircleYellow = Color.fromARGB(255, 250, 204, 21);
const Color kCirclePurple = Color.fromARGB(255, 147, 51, 234);
const Color kCircleRed = Color.fromARGB(255, 220, 20, 60);
const Color kCircleGreen = Color.fromARGB(255, 34, 197, 94);
const Color kCircleBlue = Color.fromARGB(255, 30, 144, 255);
const Color kCircleGrey = Color.fromARGB(255, 128, 128, 128);
