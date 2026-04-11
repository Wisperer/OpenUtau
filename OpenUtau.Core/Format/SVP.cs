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

                var pitdPoints = new List<(int x, int y)>();
                var dynPoints = new List<(int x, int y)>();
                var tenPoints = new List<(int x, int y)>();
                var brePoints = new List<(int x, int y)>();
                var genPoints = new List<(int x, int y)>(); 
                var vocalModePoints = new Dictionary<string, List<(int x, int y)>>(); 

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
                            if (svpNote.musicalType != "singing") continue;

                            int tickOn = (int)Math.Round((svpNote.onset + offsetBlicks) / blicksPerTick);
                            int tickOff = (int)Math.Round((svpNote.onset + svpNote.duration + offsetBlicks) / blicksPerTick);
                            int duration = Math.Max(tickOff - tickOn, 1);

                            var note = project.CreateNote(svpNote.pitch, tickOn, duration);
                            note.lyric = string.IsNullOrEmpty(svpNote.lyrics) ? "a" : svpNote.lyrics;

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

                            part.notes.Add(note);
                        }
                    }

                    if (group.parameters != null) {
                        ParseFlatCurve(group.parameters.pitchDelta?.points, pitdPoints, offsetBlicks, blicksPerTick, 1f);
                        ParseFlatCurve(group.parameters.loudness?.points, dynPoints, offsetBlicks, blicksPerTick, 10f); 
                        ParseFlatCurve(group.parameters.tension?.points, tenPoints, offsetBlicks, blicksPerTick, 100f);
                        ParseFlatCurve(group.parameters.breathiness?.points, brePoints, offsetBlicks, blicksPerTick, 100f);
                        ParseFlatCurve(group.parameters.gender?.points, genPoints, offsetBlicks, blicksPerTick, 100f); 
                    }
                    // vocal mode curves
                    if (group.vocalModes != null) {
                        foreach (var kvp in group.vocalModes) {
                            if (!vocalModePoints.ContainsKey(kvp.Key)) {
                                vocalModePoints[kvp.Key] = new List<(int x, int y)>();
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
                            ParseFlatCurve(svpTrack.mainRef.systemPitchDelta.points, pitdPoints, svpTrack.mainRef.blickOffset, blicksPerTick, 1f);
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
                pitdPoints.RemoveAll(pt => pt.y == 40 || pt.y == -40);
                FinalizeCurve(project, part, Ustx.PITD, pitdPoints);
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

        private static void ParseFlatCurve(List<double> points, List<(int x, int y)> outPoints, long offsetBlicks, double blicksPerTick, float multiplier) {
            if (points == null || points.Count < 2) return;

            for (int i = 0; i < points.Count - 1; i += 2) {
                int tick = Math.Max(0, (int)Math.Round((points[i] + offsetBlicks) / blicksPerTick));
                int val = (int)Math.Round(points[i + 1] * multiplier);
                outPoints.Add((tick, val));
            }
        }

        private static void FinalizeCurve(UProject project, UVoicePart part, string abbr, List<(int x, int y)> points) {
            if (points.Count == 0) return;

            var curve = GetCurve(project, part, abbr);
            if (curve == null) return; 

            var sortedPoints = points.OrderBy(p => p.x).ToList();
            var smoothedPoints = SmoothCurve(sortedPoints, 15);

            int min = (int)(curve.descriptor?.min ?? -1200);
            int max = (int)(curve.descriptor?.max ?? 1200);
            int lastTick = -999;
            
            foreach (var pt in smoothedPoints) {
                if (pt.x - lastTick >= 5) {
                    int yClamped = Math.Max(min, Math.Min(max, pt.y)); 
                    curve.xs.Add(pt.x);
                    curve.ys.Add(yClamped);
                    lastTick = pt.x;
                }
            }

            if (curve.xs.Count > 0 && curve.xs.Last() != smoothedPoints.Last().x) {
                int finalY = Math.Max(min, Math.Min(max, smoothedPoints.Last().y));
                curve.xs.Add(smoothedPoints.Last().x);
                curve.ys.Add(finalY);
            }
        }

        private static List<(int x, int y)> SmoothCurve(List<(int x, int y)> points, int windowSize) {
            if (points.Count == 0) return points;
            var result = new List<(int x, int y)>();
            int half = windowSize / 2;
            
            for (int i = 0; i < points.Count; i++) {
                int sum = 0;
                int count = 0;
                for (int j = Math.Max(0, i - half); j <= Math.Min(points.Count - 1, i + half); j++) {
                    sum += points[j].y;
                    count++;
                }
                result.Add((points[i].x, (int)Math.Round((double)sum / count)));
            }
            return result;
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
        }
    }
}