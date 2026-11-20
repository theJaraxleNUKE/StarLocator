// StarLocator phone mount — parametric OpenSCAD source
// -----------------------------------------------------------------------------
// The phone lies SCREEN-DOWN on a ramp with its bottom edge against the lip, so the
// back camera looks along the ramp's surface normal:
//     camera elevation = 90° - ramp angle      (60° elevation -> 30° ramp)
// The exact angle is not critical: the app's one-time calibration absorbs it. What
// matters is that the phone sits the SAME way every time (same case, fully seated).
//
// The model is generated in its PRINT orientation: lying on its side, the triangular
// profile flat on the bed, so it prints without supports. Units: mm.
// -----------------------------------------------------------------------------

camera_elevation = 60;    // degrees above the horizon the back camera points at
deck_length      = 130;   // ramp length along the slope (phone overhangs past it: camera end stays free)
width            = 100;   // across the ramp; wider than any phone + case
front_height     = 15;    // keeps the phone's bottom edge / speaker off the ground
wall             = 4;     // wall / deck thickness
lip_height       = 10;    // stop for the phone's bottom edge, measured from the deck surface
lip_thickness    = 5;
speaker_slot     = 40;    // gap in the middle of the lip for the bottom speaker / USB port
pad_depth        = 1.0;   // recesses for adhesive felt/rubber pads that protect the screen
pad_len          = 24;    // pad size along the slope
pad_wid          = 14;    // pad size across

ramp = 90 - camera_elevation;          // ramp angle from horizontal
L    = deck_length * cos(ramp);        // horizontal footprint of the ramp
H    = front_height + deck_length * sin(ramp);
P0   = [0, front_height];              // front edge of the deck surface (2D: x = along slope, y = height)

// 2D side profile (x = horizontal, y = up in use)
module outer() { polygon([[0, 0], [L, 0], [L, H], P0]); }

module profile() {
    union() {
        difference() {               // hollow triangular frame: base, back, deck
            outer();
            offset(delta = -wall) outer();
        }
        // vertical brace in the middle
        translate([L / 2 - wall / 2, 0]) square([wall, front_height + (L / 2) * tan(ramp)]);
        // lip, perpendicular to the deck, just in front of the deck's front edge
        translate(P0) rotate(ramp) translate([-lip_thickness, 0]) square([lip_thickness, lip_height]);
        // fill under the lip so it is supported down to the frame
        translate(P0) rotate(ramp) translate([-lip_thickness, -wall]) square([lip_thickness, wall]);
    }
}

// In deck-local coordinates: x along the slope from the front edge, y along the outward normal.
module deck_local() { translate(P0) rotate([0, 0, ramp]) children(); }

difference() {
    linear_extrude(height = width) profile();

    // speaker / USB slot through the lip (only above the deck surface)
    deck_local() translate([-lip_thickness - 1, 0.01, width / 2 - speaker_slot / 2])
        cube([lip_thickness + 1.01, lip_height + 1, speaker_slot]);

    // four shallow pad recesses on the deck surface
    for (s = [22, deck_length - 22], z = [22, width - 22])
        deck_local() translate([s - pad_len / 2, -pad_depth, z - pad_wid / 2])
            cube([pad_len, pad_depth + 1, pad_wid]);
}

echo(str("ramp angle = ", ramp, " deg, camera elevation = ", camera_elevation,
         " deg, footprint = ", L, " x ", width, " mm, height = ", H, " mm"));
