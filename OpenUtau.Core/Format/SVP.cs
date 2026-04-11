using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Newtonsoft.Json;
using OpenUtau.Core.Ustx;
using OpenUtau.Core;

namespace OpenUtau.Core.Format {
    public static class SVP {
        public static UProject Load(string svpFilePath) {
            try {
                var json = File.ReadAllText(svpFilePath);
                var svpProject = JsonConvert.DeserializeObject<SVPProject>(json);
                
                if (svpProject == null) {
                    throw new FileFormatException("Failed to parse SVP file");
                }

                return ConvertToUstx(svpProject, svpFilePath);
            } catch (Exception ex) {
                throw new FileFormatException($"Error loading SVP file: {ex.Message}", ex);
            }
        }

        private static UProject ConvertToUstx(SVPProject svpProject, string svpFilePath) {
            var project = new UProject {};
            Ustx.AddDefaultExpressions(project); 

            double blicksPerTick = 705600000.0 / project.resolution;
            // tempo and time sig
            project.timeSignatures = svpProject.time?.meter?.Select(m =>
                new UTimeSignature(m.index, m.numerator, m.denominator))
                .ToList() ?? new List<UTimeSignature> { new UTimeSignature(0, 4, 4) };

            project.tempos = svpProject.time?.tempo?.Select(t =>
                new UTempo((int)Math.Round(t.position / blicksPerTick), t.bpm)
            ).ToList() ?? new List<UTempo> { new UTempo(0, 120) };

            var timeAxis = new TimeAxis();
            timeAxis.BuildSegments(project);

            var libraryGroups = new Dictionary<string, SVPGroup>();
            if (svpProject.library != null) {
                foreach (var group in svpProject.library) {
                    if (!string.IsNullOrEmpty(group.uuid)) {
                        libraryGroups[group.uuid] = group;
                    }
                }
            }
            // group and parts
            foreach (var svpTrack in svpProject.tracks ?? new List<SVPTrack>()) {
                string singerName = "";
                bool trackHasContent = false;

                var track = new UTrack(project) { 
                    TrackNo = project.tracks.Count,
                    TrackName = svpTrack.name ?? "Unnamed Track" 
                };
                
                var part = new UVoicePart {
                    name = svpTrack.name ?? "Part",
                    position = 0,
                    trackNo = track.TrackNo
                };

                var manualPitchPoints = new List<(double x, double y)>();
                var aiPitchPoints = new List<(double x, double y)>();
                var manualNoteRanges = new List<(int start, int end)>();
                var dynPoints = new List<(double x, double y)>();
                var tenPoints = new List<(double x, double y)>();
                var brePoints = new List<(double x, double y)>();
                var genPoints = new List<(double x, double y)>(); 
                var vocalModePoints = new Dictionary<string, List<(double x, double y)>>();

                var phonemeQueue = new Queue<string>();
                // audio track
                void TryParseAudio(SVPMRef mRef) {
                    if (mRef == null || !mRef.isInstrumental || mRef.audio == null || string.IsNullOrWhiteSpace(mRef.audio.filename)) return;

                    string audioFile = mRef.audio.filename;
                    if (!Path.IsPathRooted(audioFile)) {
                        audioFile = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(svpFilePath), audioFile));
                    }

                    if (!File.Exists(audioFile)) {
                        return; 
                    }

                    var wavePart = new UWavePart {
                        name = Path.GetFileName(audioFile),
                        FilePath = audioFile,
                        position = Math.Max(0, (int)Math.Round(mRef.blickOffset / blicksPerTick)),
                        trackNo = track.TrackNo
                    };
                    
                    project.parts.Add(wavePart);
                    trackHasContent = true;
                }

                void ExtractGroup(SVPGroup group, long offsetBlicks) {
                    if (group == null) return;

                    if (group.notes != null) {
                        foreach (var svpNote in group.notes) {
                            if (svpNote.musicalType != "singing" && svpNote.musicalType != "rap") continue;

                            int tickOn = (int)Math.Round((svpNote.onset + offsetBlicks) / blicksPerTick);
                            int tickOff = (int)Math.Round((svpNote.onset + svpNote.duration + offsetBlicks) / blicksPerTick);
                            int duration = Math.Max(tickOff - tickOn, 1);

                            var note = project.CreateNote(svpNote.pitch, tickOn, duration);
                            note.lyric = string.IsNullOrEmpty(svpNote.lyrics) ? "a" : svpNote.lyrics;

                            if (svpNote.instantMode.HasValue && svpNote.instantMode.Value == false) {
                                manualNoteRanges.Add((tickOn, tickOff));
                            }

                            if (note.lyric == "-") {
                                note.lyric = "+~";
                                // convert synthv note override phoneme to ou's format
                            } else if (note.lyric.StartsWith(".")) {
                                note.lyric = $"[{note.lyric.Substring(1)}]";
                            }
                            // parse the overriden synthv phonemes in the top of the note to ou's format
                            if (!string.IsNullOrWhiteSpace(svpNote.phonemes)) {
                                phonemeQueue.Clear();
                                var syllables = svpNote.phonemes.Split('+').Select(s => s.Trim()).ToArray();
                                foreach (var syl in syllables) {
                                    if (!string.IsNullOrWhiteSpace(syl)) {
                                        phonemeQueue.Enqueue(syl);
                                    }
                                }
                            } else if (note.lyric != "+" && note.lyric != "+~") {
                                phonemeQueue.Clear();
                            }

                            if (phonemeQueue.Count > 0) {
                                string currentSyl = phonemeQueue.Dequeue();
                                if (note.lyric.StartsWith("[") && note.lyric.EndsWith("]")) {
                                    note.lyric = $"[{currentSyl}]";
                                } else {
                                    note.lyric += $" [{currentSyl}]";
                                }
                            }

                            note.pitch.data.Clear(); 
                            note.vibrato.length = 0;

                            
                            var activeAttrs = svpNote.attributes ?? svpNote.systemAttributes;

                            if (activeAttrs != null) {
                                // vibrato mapping from the settings
                                double vbrDepth = activeAttrs.dF0Vbr ?? 0;
                                if (vbrDepth > 0) {
                                    note.vibrato.depth = (float)(vbrDepth * 100);
                                    double vbrFreq = activeAttrs.fF0Vbr ?? 5.5; 
                                    note.vibrato.period = (float)(1000.0 / (vbrFreq > 0 ? vbrFreq : 5.5));
                                    double vbrStartSec = activeAttrs.tF0VbrStart ?? 0.25;
                                    double noteDurationSec = (duration * project.resolution) / 705600000.0; // Rough Blick-to-second estimate
                                    double activeVbrSec = Math.Max(0, noteDurationSec - vbrStartSec);
                                    double lengthPercent = (activeVbrSec / noteDurationSec) * 100.0;
                                    note.vibrato.length = (float)Math.Max(0, Math.Min(100, lengthPercent));
                                }

                                // portamento mapping
                                double portamentoLeft = activeAttrs.tF0Left ?? 0.04;
                                double portamentoRight = activeAttrs.tF0Right ?? 0.04;
                                double portamentoOffset = activeAttrs.tF0Offset ?? 0.0;

                                float msOffset = (float)(portamentoOffset * 1000.0);
                                float msLeft = (float)(portamentoLeft * 1000.0);
                                float msRight = (float)(portamentoRight * 1000.0);

                                float yLeft = (float)((activeAttrs.dF0Left ?? 0.0) * 10.0);
                                float yRight = (float)((activeAttrs.dF0Right ?? 0.0) * 10.0);

                                float xStart = -msLeft + msOffset;
                                float xEnd = msRight + msOffset;

                                note.pitch.AddPoint(new PitchPoint(xStart, yLeft)); 
                                note.pitch.AddPoint(new PitchPoint(xEnd, yRight));
                                
                            }

                            part.notes.Add(note);
                        }
                    }

                    if (group.parameters != null) {
                        ParseFlatCurve(group.parameters.pitchDelta?.points, manualPitchPoints, offsetBlicks, blicksPerTick, 1f);
                        ParseFlatCurve(group.parameters.loudness?.points, dynPoints, offsetBlicks, blicksPerTick, 10f);
                        ParseFlatCurve(group.parameters.tension?.points, tenPoints, offsetBlicks, blicksPerTick, 100f);
                        ParseFlatCurve(group.parameters.breathiness?.points, brePoints, offsetBlicks, blicksPerTick, 100f);
                        ParseFlatCurve(group.parameters.gender?.points, genPoints, offsetBlicks, blicksPerTick, 100f); 
                    }
                    // vocal mode curves
                    if (group.vocalModes != null) {
                        foreach (var kvp in group.vocalModes) {
                            if (!vocalModePoints.ContainsKey(kvp.Key)) {
                                vocalModePoints[kvp.Key] = new List<(double x, double y)>();
                            }
                            ParseFlatCurve(kvp.Value?.points, vocalModePoints[kvp.Key], offsetBlicks, blicksPerTick, 1f);
                        }
                    }
                }

                ExtractGroup(svpTrack.mainGroup, 0);

                if (svpTrack.mainRef != null) {
                    if (svpTrack.mainRef.isInstrumental) {
                        TryParseAudio(svpTrack.mainRef);
                    } else {
                        if (svpTrack.mainRef.database != null && !string.IsNullOrWhiteSpace(svpTrack.mainRef.database.name)) {
                            singerName = svpTrack.mainRef.database.name;
                        }
                        if (svpTrack.mainRef.systemPitchDelta != null) {
                            ParseFlatCurve(svpTrack.mainRef.systemPitchDelta.points, aiPitchPoints, svpTrack.mainRef.blickOffset, blicksPerTick, 1f);
                        }
                        if (!string.IsNullOrEmpty(svpTrack.mainRef.groupID) && libraryGroups.TryGetValue(svpTrack.mainRef.groupID, out var libGroup)) {
                            ExtractGroup(libGroup, svpTrack.mainRef.blickOffset);
                        }
                    }
                }

                if (svpTrack.groups != null) {
                    foreach (var refGroup in svpTrack.groups) {
                        if (refGroup.isInstrumental) {
                            TryParseAudio(refGroup);
                        } else {
                            if (string.IsNullOrWhiteSpace(singerName) && refGroup.database != null && !string.IsNullOrWhiteSpace(refGroup.database.name)) {
                                singerName = refGroup.database.name;
                            }
                            if (!string.IsNullOrEmpty(refGroup.groupID) && libraryGroups.TryGetValue(refGroup.groupID, out var linkedGroup)) {
                                ExtractGroup(linkedGroup, refGroup.blickOffset);
                            }
                        }
                    }
                }
                if (!project.expressions.ContainsKey(Ustx.TENC)) {
                    project.RegisterExpression(new UExpressionDescriptor("tension curve", Ustx.TENC, -100, 100, 0) { type = UExpressionType.Curve });
                }
                if (!project.expressions.ContainsKey(Ustx.BREC)) {
                    project.RegisterExpression(new UExpressionDescriptor("breathiness curve", Ustx.BREC, -100, 100, 0) { type = UExpressionType.Curve });
                }
                if (!project.expressions.ContainsKey(Ustx.GENC)) {
                    project.RegisterExpression(new UExpressionDescriptor("gender curve", Ustx.GENC, -100, 100, 0) { type = UExpressionType.Curve });
                }

                aiPitchPoints.RemoveAll(pt => pt.y == 40 || pt.y == -40);
                manualPitchPoints.RemoveAll(pt => pt.y == 40 || pt.y == -40);
                FinalizeMergedPitch(project, part, Ustx.PITD, manualPitchPoints, aiPitchPoints, manualNoteRanges);
                FinalizeCurve(project, part, Ustx.DYN, dynPoints);
                FinalizeCurve(project, part, Ustx.TENC, tenPoints);
                FinalizeCurve(project, part, Ustx.BREC, brePoints);
                FinalizeCurve(project, part, Ustx.GENC, genPoints);

                foreach (var kvp in vocalModePoints) {
                    string modeName = kvp.Key;
                    string abbr = kvp.Key.ToLower();

                    if (!project.expressions.ContainsKey(abbr)) {
                        project.RegisterExpression(new UExpressionDescriptor(modeName, abbr, -200, 200, 0) {
                            type = UExpressionType.Curve 
                        });
                    }
                    FinalizeCurve(project, part, abbr, kvp.Value);
                }

                if (part.notes.Count > 0 || part.curves.Count > 0) {
                    int finalDuration = 0;
                    if (part.notes.Count > 0) {
                        finalDuration = Math.Max(finalDuration, part.notes.Max(n => n.End));
                    }
                    foreach (var c in part.curves) {
                        if (c.xs.Count > 0) {
                            finalDuration = Math.Max(finalDuration, c.xs.Last());
                        }
                    }
                    
                    track.Singer = USinger.CreateMissing(string.IsNullOrWhiteSpace(singerName) ? "" : singerName);
                    part.Duration = finalDuration;
                    project.parts.Add(part);
                    trackHasContent = true;
                }

                if (trackHasContent) {
                    project.tracks.Add(track);
                }
            }

            project.ValidateFull();
            return project;
        }

        private static UCurve GetCurve(UProject uproject, UVoicePart upart, string abbr) {
            var curve = upart.curves.Find(c => c.abbr == abbr);
            if (curve == null) {
                if (uproject.expressions.TryGetValue(abbr, out var desc)) {
                    curve = new UCurve(desc);
                    upart.curves.Add(curve);
                }
            }
            return curve;
        }

        private static void ParseFlatCurve(List<double> points, List<(double x, double y)> outPoints, long offsetBlicks, double blicksPerTick, float multiplier) {
            if (points == null || points.Count < 2) return;

            for (int i = 0; i < points.Count - 1; i += 2) {
                double tick = (points[i] + offsetBlicks) / blicksPerTick;
                double val = points[i + 1] * multiplier;
                outPoints.Add((tick, val));
            }
        }

        private static void FinalizeCurve(UProject project, UVoicePart part, string abbr, List<(double x, double y)> points) {
            if (points.Count == 0) return;

            var curve = GetCurve(project, part, abbr);
            if (curve == null) return; 

            var sortedPoints = points.OrderBy(p => p.x).ToList();
            int n = sortedPoints.Count;
            
            if (n < 2) return;

            // Non-Uniform Tangents (Slopes)
            double[] m = new double[n];
            for (int i = 0; i < n; i++) {
                if (i == 0) {
                    double dx = sortedPoints[1].x - sortedPoints[0].x;
                    m[i] = dx > 0 ? (sortedPoints[1].y - sortedPoints[0].y) / dx : 0;
                } else if (i == n - 1) {
                    double dx = sortedPoints[n - 1].x - sortedPoints[n - 2].x;
                    m[i] = dx > 0 ? (sortedPoints[n - 1].y - sortedPoints[n - 2].y) / dx : 0;
                } else {
                    double dx1 = sortedPoints[i].x - sortedPoints[i - 1].x;
                    double dx2 = sortedPoints[i + 1].x - sortedPoints[i].x;
                    double dy1 = sortedPoints[i].y - sortedPoints[i - 1].y;
                    double dy2 = sortedPoints[i + 1].y - sortedPoints[i].y;
                    
                    double m1 = dx1 > 0 ? dy1 / dx1 : 0;
                    double m2 = dx2 > 0 ? dy2 / dx2 : 0;
                    
                    m[i] = (dx1 + dx2 > 0) ? (m1 * dx2 + m2 * dx1) / (dx1 + dx2) : 0;
                }
            }

            int min = (int)(curve.descriptor?.min ?? -1200);
            int max = (int)(curve.descriptor?.max ?? 1200);

            int interval = 2;
            int startTick = Math.Max(0, (int)Math.Floor(sortedPoints[0].x));
            int endTick = (int)Math.Ceiling(sortedPoints[n - 1].x);

            int currentPt = 0;
            for (int x = startTick; x <= endTick; x += interval) {
                while (currentPt < n - 2 && sortedPoints[currentPt + 1].x < x) {
                    currentPt++;
                }

                double x0 = sortedPoints[currentPt].x;
                double y0 = sortedPoints[currentPt].y;
                double x1 = sortedPoints[currentPt + 1].x;
                double y1 = sortedPoints[currentPt + 1].y;
                double dx = x1 - x0;

                double y;
                if (dx <= 0) {
                    y = y1;
                } else if (x <= x0) {
                    y = y0;
                } else if (x >= x1) {
                    y = y1;
                } else {
                    // Cubic Hermite Spline Formula
                    double t = (x - x0) / dx;
                    double t2 = t * t;
                    double t3 = t2 * t;

                    double h00 = 2 * t3 - 3 * t2 + 1;
                    double h10 = t3 - 2 * t2 + t;
                    double h01 = -2 * t3 + 3 * t2;
                    double h11 = t3 - t2;

                    y = h00 * y0 + h10 * dx * m[currentPt] + h01 * y1 + h11 * dx * m[currentPt + 1];
                }

                int yClamped = Math.Max(min, Math.Min(max, (int)Math.Round(y)));
                curve.xs.Add(x);
                curve.ys.Add(yClamped);
            }
        }

        private static double GetY(List<(double x, double y)> pts, double targetX) {
            if (pts == null || pts.Count == 0) return 0;
            if (pts.Count == 1) return pts[0].y;
            if (targetX <= pts[0].x) return pts[0].y;
            if (targetX >= pts.Last().x) return pts.Last().y;

            int i = 0;
            while (i < pts.Count - 2 && pts[i + 1].x <= targetX) {
                i++;
            }

            double x0 = pts[i].x;
            double y0 = pts[i].y;
            double x1 = pts[i + 1].x;
            double y1 = pts[i + 1].y;
            double dx = x1 - x0;
            
            if (dx <= 0) return y1; 

            double secant = (y1 - y0) / dx;
            double m0 = 0;

            if (i == 0) {
                m0 = secant;
            } else {
                double dxPrev = x0 - pts[i - 1].x;
                double secantPrev = dxPrev > 0 ? (y0 - pts[i - 1].y) / dxPrev : 0;
                if (secantPrev * secant > 0) {
                    m0 = (secantPrev + secant) * 0.5;
                    m0 = Math.Sign(m0) * Math.Min(Math.Abs(m0), Math.Min(3.0 * Math.Abs(secantPrev), 3.0 * Math.Abs(secant)));
                }
            }

            // Clamped Tangent M1
            double m1 = 0;
            if (i + 2 >= pts.Count) {
                m1 = secant;
            } else {
                double dxNext = pts[i + 2].x - x1;
                double secantNext = dxNext > 0 ? (pts[i + 2].y - y1) / dxNext : 0;
                if (secant * secantNext > 0) {
                    m1 = (secant + secantNext) * 0.5;
                    m1 = Math.Sign(m1) * Math.Min(Math.Abs(m1), Math.Min(3.0 * Math.Abs(secant), 3.0 * Math.Abs(secantNext)));
                }
            }

            // Standard Cubic Hermite Formula
            double t = (targetX - x0) / dx;
            double t2 = t * t;
            double t3 = t2 * t;

            double h00 = 2 * t3 - 3 * t2 + 1;
            double h10 = t3 - 2 * t2 + t;
            double h01 = -2 * t3 + 3 * t2;
            double h11 = t3 - t2;

            return h00 * y0 + h10 * dx * m0 + h01 * y1 + h11 * dx * m1;
        }

        private static void FinalizeMergedPitch(UProject project, UVoicePart part, string abbr, List<(double x, double y)> manualPt, List<(double x, double y)> aiPt, List<(int start, int end)> manualNoteRanges) {
            if (manualPt.Count == 0 && aiPt.Count == 0) return;

            var curve = GetCurve(project, part, abbr);
            if (curve == null) return;

            var sortedManual = manualPt.OrderBy(p => p.x).ToList();
            var sortedAi = aiPt.OrderBy(p => p.x).ToList();

            double minX = double.MaxValue;
            double maxX = double.MinValue;

            if (sortedManual.Count > 0) {
                minX = Math.Min(minX, sortedManual.First().x);
                maxX = Math.Max(maxX, sortedManual.Last().x);
            }
            if (sortedAi.Count > 0) {
                minX = Math.Min(minX, sortedAi.First().x);
                maxX = Math.Max(maxX, sortedAi.Last().x);
            }

            int startTick = Math.Max(0, (int)Math.Floor(minX));
            int endTick = (int)Math.Ceiling(maxX);

            int minVal = (int)(curve.descriptor?.min ?? -1200);
            int maxVal = (int)(curve.descriptor?.max ?? 1200);

            for (int x = startTick; x <= endTick; x += 5) { 
                bool inManual = sortedManual.Count > 0 && x >= sortedManual.First().x && x <= sortedManual.Last().x;
                bool inAi = sortedAi.Count > 0 && x >= sortedAi.First().x && x <= sortedAi.Last().x;

                if (!inManual && !inAi) continue;

                double yManual = inManual ? GetY(sortedManual, x) : 0;
                double yAi = inAi ? GetY(sortedAi, x) : 0;

                bool isBlueNote = manualNoteRanges.Any(r => x >= r.start && x <= r.end);
                if (isBlueNote) {
                    yAi = 0; 
                }
                
                int finalY = Math.Max(minVal, Math.Min(maxVal, (int)Math.Round(yManual + yAi)));
                
                if (curve.xs.Count == 0 || x > curve.xs.Last()) {
                    curve.xs.Add(x);
                    curve.ys.Add(finalY);
                }
            }
        }

        private class SVPProject {
            public int version { get; set; }
            public SVPTime time { get; set; }
            public List<SVPGroup> library { get; set; }
            public List<SVPTrack> tracks { get; set; }
        }

        private class SVPTime {
            public List<SVPMeter> meter { get; set; }
            public List<SVPTempo> tempo { get; set; }
        }

        private class SVPMeter {
            public int index { get; set; }
            public int numerator { get; set; }
            public int denominator { get; set; }
        }

        private class SVPTempo {
            public long position { get; set; }
            public double bpm { get; set; }
        }

        private class SVPGroup {
            public string uuid { get; set; }
            public List<SVPNote> notes { get; set; }
            public SVPParameters parameters { get; set; }
            public Dictionary<string, SVPCurve> vocalModes { get; set; } 
        }

        private class SVPDatabase {
            public string name { get; set; }
        }

        private class SVPParameters {
            public SVPCurve pitchDelta { get; set; }
            public SVPCurve loudness { get; set; }
            public SVPCurve tension { get; set; }
            public SVPCurve breathiness { get; set; }
            public SVPCurve gender { get; set; } 
        }

        private class SVPCurve {
            public string mode { get; set; }
            public List<double> points { get; set; } 
        }

        private class SVPTrack {
            public string name { get; set; }
            public SVPGroup mainGroup { get; set; }
            public SVPMRef mainRef { get; set; }
            public List<SVPMRef> groups { get; set; }
        }

        private class SVPMRef {
            public string groupID { get; set; }
            public long blickOffset { get; set; }
            public SVPCurve systemPitchDelta { get; set; }
            public SVPDatabase database { get; set; }
            public bool isInstrumental { get; set; }
            public SVPAudio audio { get; set; }
        }

        private class SVPAudio {
            public string filename { get; set; }
        }

        private class SVPNote {
            public string musicalType { get; set; }
            public long onset { get; set; }
            public long duration { get; set; }
            public string lyrics { get; set; }
            public string phonemes { get; set; }
            public int pitch { get; set; }
            public bool? instantMode { get; set; }
            public SVPAttributes attributes { get; set; }
            public SVPAttributes systemAttributes { get; set; }
        }

        private class SVPAttributes {
            public double? tF0Offset { get; set; }
            public double? tF0Left { get; set; }
            public double? tF0Right { get; set; }
            public double? dF0Left { get; set; }
            public double? dF0Right { get; set; }
            public double? dF0Vbr { get; set; }
            public double? fF0Vbr { get; set; }
            public double? tF0VbrStart { get; set; }
        }
    }
}