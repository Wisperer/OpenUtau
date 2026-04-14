using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenUtau.Core.Ustx;
using OpenUtau.Core;

namespace OpenUtau.Core.Format {
    // Synthv Studio SVR2 (SV1)
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
                var voicingPoints = new List<(double x, double y)>();
                var toneShiftPoints = new List<(double x, double y)>();
                var vocalModePoints = new Dictionary<string, List<(double x, double y)>>();
                var phonemeQueue = new Queue<string>();
                
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

                void ExtractGroup(SVPGroup group, long offsetBlicks, SVPVoice voice = null) {
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
                            } else if (note.lyric.StartsWith(".")) {
                                note.lyric = $"[{note.lyric.Substring(1)}]";
                            }
                            
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

                            double? dF0Vbr = activeAttrs?.dF0Vbr ?? voice?.dF0Vbr;
                            double? fF0Vbr = activeAttrs?.fF0Vbr ?? voice?.fF0Vbr;
                            double? tF0VbrStart = activeAttrs?.tF0VbrStart ?? voice?.tF0VbrStart;

                            double vbrDepth = dF0Vbr ?? 0;
                            if (vbrDepth > 0) {
                                note.vibrato.depth = (float)(vbrDepth * 100);
                                double vbrFreq = fF0Vbr ?? 5.5; 
                                note.vibrato.period = (float)(1000.0 / (vbrFreq > 0 ? vbrFreq : 5.5));
                                double vbrStartSec = tF0VbrStart ?? 0.25;
                                double noteDurationSec = (duration * project.resolution) / 705600000.0; 
                                double activeVbrSec = Math.Max(0, noteDurationSec - vbrStartSec);
                                double lengthPercent = (activeVbrSec / noteDurationSec) * 100.0;
                                note.vibrato.length = (float)Math.Max(0, Math.Min(100, lengthPercent));
                            }

                            double? tF0Left = activeAttrs?.tF0Left ?? voice?.tF0Left;
                            double? tF0Right = activeAttrs?.tF0Right ?? voice?.tF0Right;
                            double? tF0Offset = activeAttrs?.tF0Offset ?? voice?.tF0Offset;
                            double? dF0Left = activeAttrs?.dF0Left ?? voice?.dF0Left;
                            double? dF0Right = activeAttrs?.dF0Right ?? voice?.dF0Right;

                            double portamentoLeft = tF0Left ?? 0.04;
                            double portamentoRight = tF0Right ?? 0.04;
                            double portamentoOffset = tF0Offset ?? 0.0;

                            float msOffset = (float)(portamentoOffset * 1000.0);
                            float msLeft = (float)(portamentoLeft * 1000.0);
                            float msRight = (float)(portamentoRight * 1000.0);

                            float yLeft = (float)((dF0Left ?? 0.0) * 10.0);
                            float yRight = (float)((dF0Right ?? 0.0) * 10.0);

                            float xStart = -msLeft + msOffset;
                            float xEnd = msRight + msOffset;

                            note.pitch.AddPoint(new PitchPoint(xStart, yLeft)); 
                            note.pitch.AddPoint(new PitchPoint(xEnd, yRight));

                            // Per-Note Vocal Mode Overrides
                            if (activeAttrs?.vocalModeParams != null) {
                                foreach (var kvp in activeAttrs.vocalModeParams) {
                                    string modeName = kvp.Key;
                                    if (!vocalModePoints.ContainsKey(modeName)) {
                                        vocalModePoints[modeName] = new List<(double x, double y)>();
                                    }
                                    vocalModePoints[modeName].Add((tickOn, kvp.Value));
                                    vocalModePoints[modeName].Add((tickOff, kvp.Value));
                                }
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
                        ParseFlatCurve(group.parameters.voicing?.points, voicingPoints, offsetBlicks, blicksPerTick, 100f);
                        ParseFlatCurve(group.parameters.toneShift?.points, toneShiftPoints, offsetBlicks, blicksPerTick, 100f);
                    }
                    
                    // Track-level default vocal modes
                    if (voice?.vocalModeParams != null) {
                        foreach (var kvp in voice.vocalModeParams) {
                            string modeName = kvp.Key;
                            if (!vocalModePoints.ContainsKey(modeName)) {
                                vocalModePoints[modeName] = new List<(double x, double y)>();
                            }
                            if (vocalModePoints[modeName].Count == 0) {
                                double startTick = Math.Max(0, offsetBlicks / blicksPerTick);
                                vocalModePoints[modeName].Add((startTick, kvp.Value));
                                vocalModePoints[modeName].Add((startTick + 10, kvp.Value));
                            }
                        }
                    }

                    // Bulletproof JToken parsing: handles both Curves and static Sliders
                    if (group.vocalModes != null) {
                        foreach (var kvp in group.vocalModes) {
                            string modeName = kvp.Key;
                            if (!vocalModePoints.ContainsKey(modeName)) {
                                vocalModePoints[modeName] = new List<(double x, double y)>();
                            }
                            
                            if (kvp.Value.Type == JTokenType.Object) {
                                var curve = kvp.Value.ToObject<SVPCurve>();
                                ParseFlatCurve(curve?.points, vocalModePoints[modeName], offsetBlicks, blicksPerTick, 1f);
                            } else if (kvp.Value.Type == JTokenType.Float || kvp.Value.Type == JTokenType.Integer) {
                                double val = kvp.Value.Value<double>();
                                double startTick = Math.Max(0, offsetBlicks / blicksPerTick);
                                vocalModePoints[modeName].Add((startTick, val));
                                vocalModePoints[modeName].Add((startTick + 10, val));
                            }
                        }
                    }
                }

                ExtractGroup(svpTrack.mainGroup, 0, svpTrack.mainRef?.voice);

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
                            ExtractGroup(libGroup, svpTrack.mainRef.blickOffset, svpTrack.mainRef.voice);
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
                                ExtractGroup(linkedGroup, refGroup.blickOffset, refGroup.voice);
                            }
                        }
                    }
                }
                
                if (!project.expressions.ContainsKey(Ustx.TENC)) project.RegisterExpression(new UExpressionDescriptor("tension (curve)", Ustx.TENC, -100, 100, 0) { type = UExpressionType.Curve });
                if (!project.expressions.ContainsKey(Ustx.BREC)) project.RegisterExpression(new UExpressionDescriptor("breathiness (curve)", Ustx.BREC, -100, 100, 0) { type = UExpressionType.Curve });
                if (!project.expressions.ContainsKey(Ustx.GENC)) project.RegisterExpression(new UExpressionDescriptor("gender (curve)", Ustx.GENC, -100, 100, 0) { type = UExpressionType.Curve });
                if (!project.expressions.ContainsKey(Ustx.VOIC)) project.RegisterExpression(new UExpressionDescriptor("voicing (curve)", Ustx.VOIC, -100, 100, 0) { type = UExpressionType.Curve });
                if (!project.expressions.ContainsKey(Ustx.SHFC)) project.RegisterExpression(new UExpressionDescriptor("tone shift (curve)", Ustx.SHFC, -100, 100, 0) { type = UExpressionType.Curve });

                aiPitchPoints.RemoveAll(pt => pt.y == 40 || pt.y == -40);
                manualPitchPoints.RemoveAll(pt => pt.y == 40 || pt.y == -40);
                
                FinalizeMergedPitch(project, part, Ustx.PITD, manualPitchPoints, aiPitchPoints, manualNoteRanges);
                FinalizeCurve(project, part, Ustx.DYN, dynPoints);
                FinalizeCurve(project, part, Ustx.TENC, tenPoints);
                FinalizeCurve(project, part, Ustx.BREC, brePoints);
                FinalizeCurve(project, part, Ustx.GENC, genPoints);
                FinalizeCurve(project, part, Ustx.VOIC, voicingPoints);
                FinalizeCurve(project, part, Ustx.SHFC, toneShiftPoints);

                foreach (var kvp in vocalModePoints) {
                    string modeName = kvp.Key;
                    string abbr = kvp.Key.ToLower();
                    if (!project.expressions.ContainsKey(abbr)) {
                        project.RegisterExpression(new UExpressionDescriptor(modeName, abbr, -200, 200, 0) { type = UExpressionType.Curve });
                    }
                    FinalizeCurve(project, part, abbr, kvp.Value);
                }

                if (part.notes.Count > 0 || part.curves.Count > 0) {
                    int finalDuration = 0;
                    if (part.notes.Count > 0) finalDuration = Math.Max(finalDuration, part.notes.Max(n => n.End));
                    foreach (var c in part.curves) if (c.xs.Count > 0) finalDuration = Math.Max(finalDuration, c.xs.Last());
                    
                    track.Singer = USinger.CreateMissing(string.IsNullOrWhiteSpace(singerName) ? "" : singerName);
                    part.Duration = finalDuration;
                    project.parts.Add(part);
                    trackHasContent = true;
                }
                if (trackHasContent) project.tracks.Add(track);
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
                while (currentPt < n - 2 && sortedPoints[currentPt + 1].x < x) currentPt++;

                double x0 = sortedPoints[currentPt].x, y0 = sortedPoints[currentPt].y;
                double x1 = sortedPoints[currentPt + 1].x, y1 = sortedPoints[currentPt + 1].y;
                double dx = x1 - x0, y;

                if (dx <= 0) y = y1;
                else if (x <= x0) y = y0;
                else if (x >= x1) y = y1;
                else {
                    double t = (x - x0) / dx, t2 = t * t, t3 = t2 * t;
                    y = (2 * t3 - 3 * t2 + 1) * y0 + (t3 - 2 * t2 + t) * dx * m[currentPt] + (-2 * t3 + 3 * t2) * y1 + (t3 - t2) * dx * m[currentPt + 1];
                }

                curve.xs.Add(x);
                curve.ys.Add(Math.Max(min, Math.Min(max, (int)Math.Round(y))));
            }
        }

        private static double GetY(List<(double x, double y)> pts, double targetX) {
            if (pts == null || pts.Count == 0) return 0;
            if (pts.Count == 1) return pts[0].y;
            if (targetX <= pts[0].x) return pts[0].y;
            if (targetX >= pts.Last().x) return pts.Last().y;

            int i = 0;
            while (i < pts.Count - 2 && pts[i + 1].x <= targetX) i++;
            double x0 = pts[i].x, y0 = pts[i].y, x1 = pts[i + 1].x, y1 = pts[i + 1].y, dx = x1 - x0;
            if (dx <= 0) return y1; 

            double secant = (y1 - y0) / dx;
            double m0 = 0;
            if (i == 0) {
                m0 = secant;
            } else {
                double dxPrev = x0 - pts[i - 1].x;
                double secantPrev = dxPrev > 0 ? (y0 - pts[i - 1].y) / dxPrev : 0;
                if (secantPrev * secant > 0) m0 = Math.Sign((secantPrev + secant) * 0.5) * Math.Min(Math.Abs((secantPrev + secant) * 0.5), Math.Min(3.0 * Math.Abs(secantPrev), 3.0 * Math.Abs(secant)));
            }

            double m1 = 0;
            if (i + 2 >= pts.Count) {
                m1 = secant;
            } else {
                double dxNext = pts[i + 2].x - x1;
                double secantNext = dxNext > 0 ? (pts[i + 2].y - y1) / dxNext : 0;
                if (secant * secantNext > 0) m1 = Math.Sign((secant + secantNext) * 0.5) * Math.Min(Math.Abs((secant + secantNext) * 0.5), Math.Min(3.0 * Math.Abs(secant), 3.0 * Math.Abs(secantNext)));
            }

            double t = (targetX - x0) / dx, t2 = t * t, t3 = t2 * t;
            return (2 * t3 - 3 * t2 + 1) * y0 + (t3 - 2 * t2 + t) * dx * m0 + (-2 * t3 + 3 * t2) * y1 + (t3 - t2) * dx * m1;
        }

        private static void FinalizeMergedPitch(UProject project, UVoicePart part, string abbr, List<(double x, double y)> manualPt, List<(double x, double y)> aiPt, List<(int start, int end)> manualNoteRanges) {
            if (manualPt.Count == 0 && aiPt.Count == 0) return;

            var curve = GetCurve(project, part, abbr);
            if (curve == null) return;

            var sortedManual = manualPt.OrderBy(p => p.x).ToList();
            var sortedAi = aiPt.OrderBy(p => p.x).ToList();

            double minX = double.MaxValue, maxX = double.MinValue;
            if (sortedManual.Count > 0) { minX = Math.Min(minX, sortedManual.First().x); maxX = Math.Max(maxX, sortedManual.Last().x); }
            if (sortedAi.Count > 0) { minX = Math.Min(minX, sortedAi.First().x); maxX = Math.Max(maxX, sortedAi.Last().x); }

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
                if (isBlueNote) yAi = 0; 
                
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
            public List<SVPGroup> groups { get; set; }
            public List<SVPTrack> tracks { get; set; }
        }

        private class SVPTime { public List<SVPMeter> meter { get; set; } public List<SVPTempo> tempo { get; set; } }
        private class SVPMeter { public int index { get; set; } public int numerator { get; set; } public int denominator { get; set; } }
        private class SVPTempo { public long position { get; set; } public double bpm { get; set; } }
        private class SVPDatabase { public string name { get; set; } }
        private class SVPCurve { public string mode { get; set; } public List<double> points { get; set; } }
        private class SVPAudio { public string filename { get; set; } }

        private class SVPGroup {
            public string uuid { get; set; }
            public string name { get; set; }
            public List<SVPNote> notes { get; set; }
            public SVPParameters parameters { get; set; }
            public Dictionary<string, JToken> vocalModes { get; set; } 
        }

        private class SVPParameters {
            public SVPCurve pitchDelta { get; set; }
            public SVPCurve loudness { get; set; }
            public SVPCurve tension { get; set; }
            public SVPCurve breathiness { get; set; }
            public SVPCurve gender { get; set; }
            public SVPCurve voicing { get; set; }
            public SVPCurve toneShift { get; set; } 
        }

        private class SVPTrack {
            public string name { get; set; }
            public SVPGroup mainGroup { get; set; }
            public SVPMRef mainRef { get; set; }
            public List<SVPMRef> groups { get; set; }
            public SVPMRef mainGroupSV2 { get; set; }
        }

        private class SVPMRef {
            public string groupID { get; set; }
            public long blickOffset { get; set; }
            public SVPCurve systemPitchDelta { get; set; }
            public SVPDatabase database { get; set; }
            public bool isInstrumental { get; set; }
            public SVPAudio audio { get; set; }
            public SVPVoice voice { get; set; }
        }

        private class SVPVoice {
            public Dictionary<string, double> vocalModeParams { get; set; }
            public double? tF0Offset { get; set; }
            public double? tF0Left { get; set; }
            public double? tF0Right { get; set; }
            public double? dF0Left { get; set; }
            public double? dF0Right { get; set; }
            public double? dF0Vbr { get; set; }
            public double? fF0Vbr { get; set; }
            public double? tF0VbrStart { get; set; }
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
            public Dictionary<string, double> vocalModeParams { get; set; }
            public double? tF0Offset, tF0Left, tF0Right, dF0Left, dF0Right, dF0Vbr, fF0Vbr, tF0VbrStart;
        }
    }

    // Synthv Studio SVR3 (SV2)
    public static class SVP2 {
        public static UProject Load(string svpFilePath) {
            try {
                var json = File.ReadAllText(svpFilePath);
                var svpProject = JsonConvert.DeserializeObject<SVPProject>(json);
                if (svpProject == null) throw new FileFormatException("Failed to parse SV2 file");
                return ConvertToUstx(svpProject, svpFilePath);
            } catch (Exception ex) {
                throw new FileFormatException($"Error loading SV2 file: {ex.Message}", ex);
            }
        }

        // Helper to dynamically extract Vocal Mode intensity from both raw floats and complex objects
        private static double ParseVocalModeParam(JToken token) {
            if (token == null) return 0;
            if (token.Type == JTokenType.Object) {
                return token["timbre"]?.Value<double>() ?? 0;
            } else if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer) {
                return token.Value<double>();
            }
            return 0;
        }

        private static UProject ConvertToUstx(SVPProject svpProject, string svpFilePath) {
            var project = new UProject {};
            Ustx.AddDefaultExpressions(project); 

            double blicksPerTick = 705600000.0 / project.resolution;
            
            project.timeSignatures = svpProject.time?.meter?.Select(m => new UTimeSignature(Math.Max(0, m.index), m.numerator, m.denominator)).ToList();
            project.timeSignatures = (project.timeSignatures == null || project.timeSignatures.Count == 0) ? new List<UTimeSignature> { new UTimeSignature(0, 4, 4) } : project.timeSignatures;

            project.tempos = svpProject.time?.tempo?.Select(t => new UTempo(Math.Max(0, (int)Math.Round(t.position / blicksPerTick)), t.bpm)).ToList();
            project.tempos = (project.tempos == null || project.tempos.Count == 0) ? new List<UTempo> { new UTempo(0, 120) } : project.tempos;

            var timeAxis = new TimeAxis();
            timeAxis.BuildSegments(project);

            var libraryGroups = new Dictionary<string, SVPGroup>();
            if (svpProject.library != null) {
                foreach (var group in svpProject.library) {
                    if (!string.IsNullOrEmpty(group.uuid)) libraryGroups[group.uuid] = group;
                }
            }
            
            foreach (var svpTrack in svpProject.tracks ?? new List<SVPTrack>()) {
                string singerName = "";
                bool trackHasContent = false;

                var track = new UTrack(project) { TrackNo = project.tracks.Count, TrackName = svpTrack.name ?? "Unnamed Track" };
                var part = new UVoicePart { name = svpTrack.name ?? "Part", position = 0, trackNo = track.TrackNo };

                var manualPitchPoints = new List<(double x, double y)>();
                var aiPitchPoints = new List<(double x, double y)>(); 
                var aiAbsolutePitchPoints = new List<(double x, double y)>(); 
                var ouEffectivePitchPoints = new List<(double x, double y)>(); 
                
                var dynPoints = new List<(double x, double y)>();
                var tenPoints = new List<(double x, double y)>();
                var brePoints = new List<(double x, double y)>();
                var genPoints = new List<(double x, double y)>();
                var voicingPoints = new List<(double x, double y)>();
                var toneShiftPoints = new List<(double x, double y)>();
                var opePoints = new List<(double x, double y)>(); 
                var vocalModePoints = new Dictionary<string, List<(double x, double y)>>();
                var phonemeQueue = new Queue<string>();

                if (svpTrack.parameters != null) {
                    ParseFlatCurve(svpTrack.parameters.pitchDelta?.points, manualPitchPoints, 0, blicksPerTick, 100f);
                    ParseFlatCurve(svpTrack.parameters.loudness?.points, dynPoints, 0, blicksPerTick, 10f);
                    ParseFlatCurve(svpTrack.parameters.tension?.points, tenPoints, 0, blicksPerTick, 100f);
                    ParseFlatCurve(svpTrack.parameters.breathiness?.points, brePoints, 0, blicksPerTick, 100f);
                    ParseFlatCurve(svpTrack.parameters.gender?.points, genPoints, 0, blicksPerTick, 100f);
                    ParseFlatCurve(svpTrack.parameters.voicing?.points, voicingPoints, 0, blicksPerTick, 100f);
                    ParseFlatCurve(svpTrack.parameters.toneShift?.points, toneShiftPoints, 0, blicksPerTick, 100f);
                    ParseFlatCurve(svpTrack.parameters.mouthOpening?.points, opePoints, 0, blicksPerTick, 100f); 
                }
                
                void TryParseAudio(SVPMRef mRef) {
                    if (mRef == null || !mRef.isInstrumental || mRef.audio == null || string.IsNullOrWhiteSpace(mRef.audio.filename)) return;
                    string audioFile = mRef.audio.filename;
                    if (!Path.IsPathRooted(audioFile)) audioFile = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(svpFilePath), audioFile));
                    if (!File.Exists(audioFile)) return; 
                    var wavePart = new UWavePart { name = Path.GetFileName(audioFile), FilePath = audioFile, position = Math.Max(0, (int)Math.Round(mRef.blickAbsoluteBegin / blicksPerTick)), trackNo = track.TrackNo };
                    project.parts.Add(wavePart);
                    trackHasContent = true;
                }

                void ExtractGroup(SVPGroup group, long absoluteOffsetBlicks, SVPVoice voice = null) {
                    if (group == null) return;

                    if (group.notes != null) {
                        foreach (var svpNote in group.notes) {
                            if (svpNote.musicalType != "singing" && svpNote.musicalType != "rap") continue;

                            int tickOn = Math.Max(0, (int)Math.Round((svpNote.onset + absoluteOffsetBlicks) / blicksPerTick));
                            int tickOff = (int)Math.Round((svpNote.onset + svpNote.duration + absoluteOffsetBlicks) / blicksPerTick);
                            if (tickOff <= 0) continue; 
                            int duration = Math.Max(tickOff - tickOn, 1);

                            var note = project.CreateNote(svpNote.pitch, tickOn, duration);
                            note.lyric = string.IsNullOrEmpty(svpNote.lyrics) ? "a" : svpNote.lyrics;

                            note.pitch.data.Clear(); 
                            note.vibrato.length = 0;

                            if (note.lyric == "-") note.lyric = "+~";
                            else if (note.lyric.StartsWith(".")) note.lyric = $"[{note.lyric.Substring(1)}]";
                            
                            if (!string.IsNullOrWhiteSpace(svpNote.phonemes)) {
                                phonemeQueue.Clear();
                                foreach (var syl in svpNote.phonemes.Split('+').Select(s => s.Trim())) if (!string.IsNullOrWhiteSpace(syl)) phonemeQueue.Enqueue(syl);
                            } else if (note.lyric != "+" && note.lyric != "+~") phonemeQueue.Clear();

                            if (phonemeQueue.Count > 0) {
                                string currentSyl = phonemeQueue.Dequeue();
                                note.lyric = (note.lyric.StartsWith("[") && note.lyric.EndsWith("]")) ? $"[{currentSyl}]" : $"{note.lyric} [{currentSyl}]";
                            }

                            var activeAttrs = svpNote.attributes ?? svpNote.systemAttributes;
                            double msOnset = timeAxis.TickPosToMsPos(tickOn);

                            double? dF0Vbr = activeAttrs?.dF0Vbr ?? voice?.dF0Vbr;
                            double? fF0Vbr = activeAttrs?.fF0Vbr ?? voice?.fF0Vbr;
                            double? tF0VbrStart = activeAttrs?.tF0VbrStart ?? voice?.tF0VbrStart;

                            double vbrDepth = dF0Vbr ?? 0;
                            if (vbrDepth > 0) {
                                note.vibrato.depth = (float)(vbrDepth * 100);
                                note.vibrato.period = (float)(1000.0 / (fF0Vbr ?? 5.5));
                                double noteSec = (duration * project.resolution) / 705600000.0;
                                note.vibrato.length = (float)Math.Max(0, Math.Min(100, (Math.Max(0, noteSec - (tF0VbrStart ?? 0.25)) / noteSec) * 100.0));
                            }

                            note.pitch.AddPoint(new PitchPoint(-10, 0));
                            note.pitch.AddPoint(new PitchPoint(40, 0));
                            
                            double tickMinus = Math.Max(0, timeAxis.MsPosToTickPos(msOnset - 10));
                            double tickPlus = timeAxis.MsPosToTickPos(msOnset + 40);
                            
                            double previousPitch = svpNote.pitch;
                            if (ouEffectivePitchPoints.Count > 0) previousPitch = ouEffectivePitchPoints.Last().y;

                            ouEffectivePitchPoints.Add((tickMinus, previousPitch));
                            ouEffectivePitchPoints.Add((tickPlus, svpNote.pitch));
                            ouEffectivePitchPoints.Add((tickOff, svpNote.pitch));

                            // Per-Note Vocal Mode Overrides (JToken Fix)
                            if (activeAttrs?.vocalModeParams != null) {
                                foreach (var kvp in activeAttrs.vocalModeParams) {
                                    string modeName = kvp.Key;
                                    double staticValue = ParseVocalModeParam(kvp.Value);
                                    if (!vocalModePoints.ContainsKey(modeName)) vocalModePoints[modeName] = new List<(double x, double y)>();
                                    vocalModePoints[modeName].Add((tickOn, staticValue));
                                    vocalModePoints[modeName].Add((tickOff, staticValue));
                                }
                            }
                            
                            part.notes.Add(note);
                        }
                    }

                    if (group.pitchControls != null && group.pitchControls.Count > 0) {
                        foreach (var pt in group.pitchControls) {
                            double tick = (pt.pos + absoluteOffsetBlicks) / blicksPerTick; 
                            aiAbsolutePitchPoints.Add((tick, pt.pitch)); 
                        }
                    }

                    if (group.parameters != null) {
                        ParseFlatCurve(group.parameters.pitchDelta?.points, manualPitchPoints, absoluteOffsetBlicks, blicksPerTick, 100f);
                        ParseFlatCurve(group.parameters.loudness?.points, dynPoints, absoluteOffsetBlicks, blicksPerTick, 10f);
                        ParseFlatCurve(group.parameters.tension?.points, tenPoints, absoluteOffsetBlicks, blicksPerTick, 100f);
                        ParseFlatCurve(group.parameters.breathiness?.points, brePoints, absoluteOffsetBlicks, blicksPerTick, 100f);
                        ParseFlatCurve(group.parameters.gender?.points, genPoints, absoluteOffsetBlicks, blicksPerTick, 100f);
                        ParseFlatCurve(group.parameters.voicing?.points, voicingPoints, absoluteOffsetBlicks, blicksPerTick, 100f);
                        ParseFlatCurve(group.parameters.toneShift?.points, toneShiftPoints, absoluteOffsetBlicks, blicksPerTick, 100f);
                        ParseFlatCurve(group.parameters.mouthOpening?.points, opePoints, absoluteOffsetBlicks, blicksPerTick, 100f); 
                    }

                    // Track-level default vocal modes (JToken Fix)
                    if (voice?.vocalModeParams != null) {
                        foreach (var kvp in voice.vocalModeParams) {
                            string modeName = kvp.Key;
                            double staticValue = ParseVocalModeParam(kvp.Value);
                            if (!vocalModePoints.ContainsKey(modeName)) vocalModePoints[modeName] = new List<(double x, double y)>();
                            if (vocalModePoints[modeName].Count == 0) {
                                double startTick = Math.Max(0, absoluteOffsetBlicks / blicksPerTick);
                                vocalModePoints[modeName].Add((startTick, staticValue));
                                vocalModePoints[modeName].Add((startTick + 10, staticValue));
                            }
                        }
                    }
                    
                    if (group.vocalModes != null) {
                        foreach (var kvp in group.vocalModes) {
                            string modeName = kvp.Key;
                            if (!vocalModePoints.ContainsKey(modeName)) vocalModePoints[modeName] = new List<(double x, double y)>();
                            
                            if (kvp.Value.Type == JTokenType.Object) {
                                var curve = kvp.Value.ToObject<SVPCurve>();
                                ParseFlatCurve(curve?.points, vocalModePoints[modeName], absoluteOffsetBlicks, blicksPerTick, 1f);
                            } else if (kvp.Value.Type == JTokenType.Float || kvp.Value.Type == JTokenType.Integer) {
                                double val = kvp.Value.Value<double>();
                                double startTick = Math.Max(0, absoluteOffsetBlicks / blicksPerTick);
                                vocalModePoints[modeName].Add((startTick, val));
                                vocalModePoints[modeName].Add((startTick + 10, val));
                            }
                        }
                    }
                }

                var allRefs = svpTrack.groups ?? new List<SVPMRef>();
                var mRef = svpTrack.mainRef ?? svpTrack.mainGroupSV2;
                if (mRef != null && !allRefs.Contains(mRef)) allRefs.Insert(0, mRef);

                foreach (var reference in allRefs) {
                    if (reference == null) continue;

                    if (reference.isInstrumental) {
                        TryParseAudio(reference);
                    } else {
                        if (string.IsNullOrWhiteSpace(singerName) && reference.database != null && !string.IsNullOrWhiteSpace(reference.database.name)) {
                            singerName = reference.database.name;
                        }
                        
                        if (reference.systemPitchDelta != null) ParseFlatCurve(reference.systemPitchDelta.points, aiPitchPoints, 0, blicksPerTick, 100f);

                        if (!string.IsNullOrEmpty(reference.groupID) && libraryGroups.TryGetValue(reference.groupID, out var linkedGroup)) {
                            long absoluteOffsetBlicks = reference.blickAbsoluteBegin - reference.blickOffset;
                            ExtractGroup(linkedGroup, absoluteOffsetBlicks, reference.voice);
                        }
                    }
                }

                if (!project.expressions.ContainsKey(Ustx.TENC)) project.RegisterExpression(new UExpressionDescriptor("tension (curve)", Ustx.TENC, -100, 100, 0) { type = UExpressionType.Curve });
                if (!project.expressions.ContainsKey(Ustx.BREC)) project.RegisterExpression(new UExpressionDescriptor("breathiness (curve)", Ustx.BREC, -100, 100, 0) { type = UExpressionType.Curve });
                if (!project.expressions.ContainsKey(Ustx.GENC)) project.RegisterExpression(new UExpressionDescriptor("gender (curve)", Ustx.GENC, -100, 100, 0) { type = UExpressionType.Curve });
                if (!project.expressions.ContainsKey(Ustx.VOIC)) project.RegisterExpression(new UExpressionDescriptor("voicing (curve)", Ustx.VOIC, -100, 100, 0) { type = UExpressionType.Curve });
                if (!project.expressions.ContainsKey(Ustx.SHFC)) project.RegisterExpression(new UExpressionDescriptor("tone shift (curve)", Ustx.SHFC, -100, 100, 0) { type = UExpressionType.Curve });
                if (!project.expressions.ContainsKey("opec")) project.RegisterExpression(new UExpressionDescriptor("mouth opening (curve)", "opec", -100, 100, 0) { type = UExpressionType.Curve });

                aiPitchPoints.RemoveAll(pt => pt.y == 4000 || pt.y == -4000);
                manualPitchPoints.RemoveAll(pt => pt.y == 4000 || pt.y == -4000);
                
                FinalizeMergedPitch(project, part, Ustx.PITD, manualPitchPoints, aiPitchPoints, aiAbsolutePitchPoints, ouEffectivePitchPoints);
                FinalizeCurve(project, part, Ustx.DYN, dynPoints);
                FinalizeCurve(project, part, Ustx.TENC, tenPoints);
                FinalizeCurve(project, part, Ustx.BREC, brePoints);
                FinalizeCurve(project, part, Ustx.GENC, genPoints);
                FinalizeCurve(project, part, Ustx.VOIC, voicingPoints);
                FinalizeCurve(project, part, Ustx.SHFC, toneShiftPoints);
                FinalizeCurve(project, part, "ope", opePoints);

                foreach (var kvp in vocalModePoints) {
                    string abbr = kvp.Key.ToLower();
                    if (!project.expressions.ContainsKey(abbr)) project.RegisterExpression(new UExpressionDescriptor(kvp.Key, abbr, -200, 200, 0) { type = UExpressionType.Curve });
                    FinalizeCurve(project, part, abbr, kvp.Value);
                }

                if (part.notes.Count > 0) {
                    int finalDuration = part.notes.Max(n => n.End);
                    foreach (var c in part.curves) if (c.xs.Count > 0) finalDuration = Math.Max(finalDuration, c.xs.Last());
                    track.Singer = USinger.CreateMissing(string.IsNullOrWhiteSpace(singerName) ? "" : singerName);
                    part.Duration = finalDuration;
                    project.parts.Add(part);
                    trackHasContent = true;
                }
                if (trackHasContent) project.tracks.Add(track);
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

        private static double GetY(List<(double x, double y)> pts, double targetX) {
            if (pts == null || pts.Count == 0) return 0;
            if (pts.Count == 1 || targetX <= pts[0].x) return pts[0].y;
            if (targetX >= pts.Last().x) return pts.Last().y;

            int i = 0;
            while (i < pts.Count - 2 && pts[i + 1].x <= targetX) i++;
            double x0 = pts[i].x, y0 = pts[i].y, x1 = pts[i + 1].x, y1 = pts[i + 1].y, dx = x1 - x0;
            if (dx <= 0) return y1; 

            double secant = (y1 - y0) / dx;
            double m0 = secant;
            if (i > 0) {
                double dxPrev = x0 - pts[i - 1].x, secantPrev = dxPrev > 0 ? (y0 - pts[i - 1].y) / dxPrev : 0;
                m0 = (secantPrev * secant > 0) ? 0.5 * (secantPrev + secant) : 0; 
            }
            double m1 = secant;
            if (i + 2 < pts.Count) {
                double dxNext = pts[i + 2].x - x1, secantNext = dxNext > 0 ? (pts[i + 2].y - y1) / dxNext : 0;
                m1 = (secant * secantNext > 0) ? 0.5 * (secant + secantNext) : 0;
            }
            double t = (targetX - x0) / dx, t2 = t * t, t3 = t2 * t;
            return (2 * t3 - 3 * t2 + 1) * y0 + (t3 - 2 * t2 + t) * dx * m0 + (-2 * t3 + 3 * t2) * y1 + (t3 - t2) * dx * m1;
        }

        // PERFECT LINEAR MATH FOR SHARP PORTAMENTO SUBTRACTION
        private static double GetYRaw(List<(double x, double y)> pts, double targetX) {
            if (pts == null || pts.Count == 0) return 0;
            if (pts.Count == 1 || targetX <= pts[0].x) return pts[0].y;
            if (targetX >= pts.Last().x) return pts.Last().y;

            int i = 0;
            while (i < pts.Count - 2 && pts[i + 1].x <= targetX) i++;
            double x0 = pts[i].x, y0 = pts[i].y, x1 = pts[i + 1].x, y1 = pts[i + 1].y;
            double dx = x1 - x0;
            
            if (dx <= 0) return y1; 
            
            return y0 + ((targetX - x0) / dx) * (y1 - y0);
        }

        private static void FinalizeCurve(UProject project, UVoicePart part, string abbr, List<(double x, double y)> points) {
            if (points == null || points.Count < 2) return;
            var curve = GetCurve(project, part, abbr);
            if (curve == null) return; 

            var sortedPoints = points.OrderBy(p => p.x).ToList();
            int min = (int)(curve.descriptor?.min ?? -1200), max = (int)(curve.descriptor?.max ?? 1200);
            int startTick = Math.Max(0, (int)Math.Floor(sortedPoints[0].x)), endTick = (int)Math.Ceiling(sortedPoints.Last().x);

            for (int x = startTick; x <= endTick; x += 2) {
                double y = GetY(sortedPoints, x);
                curve.xs.Add(x);
                curve.ys.Add(Math.Max(min, Math.Min(max, (int)Math.Round(y))));
            }
        }

        private static void FinalizeMergedPitch(UProject project, UVoicePart part, string abbr, List<(double x, double y)> manualPt, List<(double x, double y)> aiPt, List<(double x, double y)> aiAbsolutePt, List<(double x, double y)> ouEffectivePt) {
            if (manualPt.Count == 0 && aiPt.Count == 0 && aiAbsolutePt.Count == 0) return;
            
            var curve = GetCurve(project, part, abbr);
            if (curve == null) return;

            var sortedManual = manualPt.OrderBy(p => p.x).ToList();
            var sortedAi = aiPt.OrderBy(p => p.x).ToList();
            var sortedAiAbs = aiAbsolutePt.OrderBy(p => p.x).ToList();
            var sortedOuEff = ouEffectivePt.OrderBy(p => p.x).ToList();

            double minX = double.MaxValue, maxX = double.MinValue;
            if (sortedManual.Count > 0) { minX = Math.Min(minX, sortedManual.First().x); maxX = Math.Max(maxX, sortedManual.Last().x); }
            if (sortedAi.Count > 0) { minX = Math.Min(minX, sortedAi.First().x); maxX = Math.Max(maxX, sortedAi.Last().x); }
            if (sortedAiAbs.Count > 0) { minX = Math.Min(minX, sortedAiAbs.First().x); maxX = Math.Max(maxX, sortedAiAbs.Last().x); }

            int minVal = (int)(curve.descriptor?.min ?? -1200);
            int maxVal = (int)(curve.descriptor?.max ?? 1200);

            var xCoords = new SortedSet<int>();
            int startX = Math.Max(0, (int)Math.Floor(minX));
            int endX = (int)Math.Ceiling(maxX);
            
            for (int x = startX; x <= endX; x += 2) xCoords.Add(x);
            foreach (var pt in sortedAiAbs) xCoords.Add(Math.Max(0, (int)Math.Round(pt.x)));
            foreach (var pt in sortedAi) xCoords.Add(Math.Max(0, (int)Math.Round(pt.x)));
            foreach (var pt in sortedOuEff) xCoords.Add(Math.Max(0, (int)Math.Round(pt.x)));

            foreach (int x in xCoords) { 
                bool inManual = sortedManual.Count > 0 && x >= sortedManual.First().x && x <= sortedManual.Last().x;
                bool inAi = sortedAi.Count > 0 && x >= sortedAi.First().x && x <= sortedAi.Last().x;
                bool inAiAbs = sortedAiAbs.Count > 0 && x >= sortedAiAbs.First().x && x <= sortedAiAbs.Last().x;
                
                if (!inManual && !inAi && !inAiAbs) continue;

                double yManual = inManual ? GetY(sortedManual, x) : 0;
                double yAiFinal = 0;

                if (inAiAbs) {
                    double absPitch = GetY(sortedAiAbs, x); 
                    double macroPitch = GetYRaw(sortedOuEff, x); 
                    
                    if (sortedOuEff.Count == 0) macroPitch = 60; 
                    
                    yAiFinal = (absPitch - macroPitch) * 100.0;
                } 
                else if (inAi) {
                    yAiFinal = GetY(sortedAi, x);
                }

                if (curve.xs.Count == 0 || x > curve.xs.Last()) {
                    curve.xs.Add(x);
                    curve.ys.Add(Math.Max(minVal, Math.Min(maxVal, (int)Math.Round(yManual + yAiFinal))));
                }
            }
        }

        private class SVPProject {
            public int version { get; set; }
            public SVPTime time { get; set; }
            public List<SVPGroup> library { get; set; }
            public List<SVPGroup> groups { get; set; }
            public List<SVPTrack> tracks { get; set; }
        }

        private class SVPTime { public List<SVPMeter> meter { get; set; } public List<SVPTempo> tempo { get; set; } }
        private class SVPMeter { public int index { get; set; } public int numerator { get; set; } public int denominator { get; set; } }
        private class SVPTempo { public long position { get; set; } public double bpm { get; set; } }
        private class SVPDatabase { public string name { get; set; } }
        private class SVPCurve { public string mode { get; set; } public List<double> points { get; set; } }
        private class SVPAudio { public string filename { get; set; } }

        private class SVPGroup {
            public string uuid { get; set; }
            public string name { get; set; }
            public List<SVPNote> notes { get; set; }
            public SVPParameters parameters { get; set; }
            public Dictionary<string, JToken> vocalModes { get; set; } 
            public List<SVPPitchControl> pitchControls { get; set; }
            public SVPVoice voice { get; set; } 
        }

        private class SVPVoice { 
            public Dictionary<string, JToken> vocalModeParams { get; set; } 
            public double? tF0Offset { get; set; }
            public double? tF0Left { get; set; }
            public double? tF0Right { get; set; }
            public double? dF0Left { get; set; }
            public double? dF0Right { get; set; }
            public double? dF0Vbr { get; set; }
            public double? fF0Vbr { get; set; }
            public double? tF0VbrStart { get; set; }
        }
        
        private class SVPPitchControl { public long pos { get; set; } public double pitch { get; set; } }

        private class SVPParameters {
            public SVPCurve pitchDelta { get; set; }
            public SVPCurve loudness { get; set; }
            public SVPCurve tension { get; set; }
            public SVPCurve breathiness { get; set; }
            public SVPCurve gender { get; set; }
            public SVPCurve voicing { get; set; }
            public SVPCurve toneShift { get; set; } 
            public SVPCurve mouthOpening { get; set; } 
        }

        private class SVPTrack {
            public string name { get; set; }
            public SVPParameters parameters { get; set; } 
            public SVPMRef mainRef { get; set; }
            public List<SVPMRef> groups { get; set; }
            public SVPMRef mainGroupSV2 { get; set; } 
        }

        private class SVPMRef {
            public string groupID { get; set; }
            public long blickOffset { get; set; }
            public long blickAbsoluteBegin { get; set; } 
            public long blickAbsoluteEnd { get; set; }
            public SVPCurve systemPitchDelta { get; set; }
            public SVPDatabase database { get; set; }
            public bool isInstrumental { get; set; }
            public SVPAudio audio { get; set; }
            public SVPVoice voice { get; set; } 
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
            public Dictionary<string, JToken> vocalModeParams { get; set; }
            public double? tF0Offset, tF0Left, tF0Right, dF0Left, dF0Right, dF0Vbr, fF0Vbr, tF0VbrStart;
        }
    }
}