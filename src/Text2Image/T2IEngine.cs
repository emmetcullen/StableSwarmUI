﻿using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json.Linq;
using StableSwarmUI.Accounts;
using StableSwarmUI.Backends;
using StableSwarmUI.Core;
using StableSwarmUI.Utils;
using StableSwarmUI.WebAPI;
using System.Diagnostics;
using System.IO;
using System.Reflection.Emit;
using static StableSwarmUI.Backends.BackendHandler;
using static StableSwarmUI.Core.Settings.User;

namespace StableSwarmUI.Text2Image
{
    /// <summary>Central core handler for text-to-image processing.</summary>
    public static class T2IEngine
    {
        /// <summary>Extension event, fired before images will be generated, just after the request is received.
        /// No backend is claimed yet.
        /// Use <see cref="InvalidOperationException"/> for a user-readable refusal message.</summary>
        public static Action<PreGenerationEventParams> PreGenerateEvent;

        public record class PreGenerationEventParams(T2IParamInput UserInput);

        /// <summary>Extension event, fired after images were generated, but before saving the result.
        /// Backend is already released, but the gen request is not marked completed.
        /// Ran before metadata is applied.
        /// Use "RefuseImage" to mark an image as refused. Note that generation previews may have already been shown to a user, if that feature is enabled on the server.
        /// Use <see cref="InvalidDataException"/> for a user-readable hard-refusal message.</summary>
        public static Action<PostGenerationEventParams> PostGenerateEvent;

        /// <summary>Paramters for <see cref="PostGenerateEvent"/>.</summary>
        public record class PostGenerationEventParams(Image Image, Dictionary<string, object> ExtraMetadata, T2IParamInput UserInput, Action RefuseImage);

        /// <summary>Extension event, fired after a batch of images were generated.
        /// Use "RefuseImage" to mark an image as removed. Note that it may have already been shown to a user, when the live result websocket API is in use.</summary>
        public static Action<PostBatchEventParams> PostBatchEvent;

        /// <summary>Parameters for <see cref="PostBatchEvent"/>.</summary>
        public record class PostBatchEventParams(T2IParamInput UserInput, ImageInBatch[] Images);

        /// <summary>Represents a single image within a batch of images, for <see cref="PostBatchEvent"/>.</summary>
        public record class ImageInBatch(Image Image, Action RefuseImage);

        /// <summary>Helper to create a function to match a backend to a user input request.</summary>
        public static Func<T2IBackendData, bool> BackendMatcherFor(T2IParamInput user_input)
        {
            if (!user_input.TryGet(T2IParamTypes.BackendType, out string type) || type == "any")
            {
                return _ => true;
            }
            string typeLow = type.ToLowerFast();
            return backend =>
            {
                if (typeLow != "any" && typeLow != backend.Backend.HandlerTypeData.ID.ToLowerFast())
                {
                    return false;
                }
                HashSet<string> features = backend.Backend.SupportedFeatures.ToHashSet();
                foreach (string flag in user_input.RequiredFlags)
                {
                    if (!features.Contains(flag))
                    {
                        return false;
                    }
                }
                return true;
            };
        }

        /// <summary>Internal handler route to create an image based on a user request.</summary>
        public static async Task CreateImageTask(T2IParamInput user_input, string batchId, Session.GenClaim claim, Action<JObject> output, Action<string> setError, bool isWS, float backendTimeoutMin, Action<Image, string> saveImages)
        {
            Stopwatch timer = Stopwatch.StartNew();
            void sendStatus()
            {
                if (isWS && user_input.SourceSession is not null)
                {
                    output(BasicAPIFeatures.GetCurrentStatusRaw(user_input.SourceSession));
                }
            }
            if (claim.ShouldCancel)
            {
                return;
            }
            T2IBackendAccess backend;
            try
            {
                PreGenerateEvent?.Invoke(new(user_input));
                claim.Extend(backendWaits: 1);
                sendStatus();
                backend = await Program.Backends.GetNextT2IBackend(TimeSpan.FromMinutes(backendTimeoutMin), user_input.Get(T2IParamTypes.Model), filter: BackendMatcherFor(user_input), session: user_input.SourceSession, notifyWillLoad: sendStatus, cancel: claim.InterruptToken);
            }
            catch (InvalidOperationException ex)
            {
                setError($"Invalid operation: {ex.Message}");
                return;
            }
            catch (TimeoutException)
            {
                setError("Timeout! All backends are occupied with other tasks.");
                return;
            }
            finally
            {
                claim.Complete(backendWaits: 1);
                sendStatus();
            }
            if (claim.ShouldCancel)
            {
                backend?.Dispose();
                return;
            }
            try
            {
                claim.Extend(liveGens: 1);
                sendStatus();
                long prepTime;
                int numImagesGenned = 0;
                long lastGenTime = 0;
                string genTimeReport = "? failed!";
                void handleImage(Image img)
                {
                    if (img is not null)
                    {
                        lastGenTime = timer.ElapsedMilliseconds;
                        genTimeReport = $"{prepTime / 1000.0:0.00} (prep) and {(lastGenTime - prepTime) / 1000.0:0.00} (gen) seconds";
                        Dictionary<string, object> extras = new() { ["generation_time"] = genTimeReport };
                        bool refuse = false;
                        PostGenerateEvent?.Invoke(new(img, extras, user_input, () => refuse = true));
                        if (refuse)
                        {
                            Logs.Info($"Refused an image.");
                        }
                        else
                        {
                            (img, string metadata) = user_input.SourceSession.ApplyMetadata(img, user_input, extras, numImagesGenned);
                            saveImages(img, metadata);
                            numImagesGenned++;
                        }
                    }
                }
                using (backend)
                {
                    if (claim.ShouldCancel)
                    {
                        return;
                    }
                    prepTime = timer.ElapsedMilliseconds;
                    await backend.Backend.GenerateLive(user_input, batchId, obj =>
                    {
                        if (obj is Image img)
                        {
                            handleImage(img);
                        }
                        else
                        {
                            output(new JObject() { ["gen_progress"] = (JToken)obj });
                        }
                    });
                    if (numImagesGenned == 0)
                    {
                        Logs.Info($"No images were generated (all refused, or failed).");
                    }
                    else if (numImagesGenned == 1)
                    {
                        Logs.Info($"Generated an image in {genTimeReport}");
                    }
                    else
                    {
                        Logs.Info($"Generated {numImagesGenned} images in {genTimeReport} ({((lastGenTime - prepTime) / numImagesGenned) / 1000.0:0.00} seconds per image)");
                    }
                }
            }
            catch (AbstractT2IBackend.PleaseRedirectException)
            {
                claim.Extend(gens: 1);
                await CreateImageTask(user_input, batchId, claim, output, setError, isWS, backendTimeoutMin, saveImages);
            }
            catch (InvalidOperationException ex)
            {
                setError($"Invalid operation: {ex.Message}");
                return;
            }
            catch (InvalidDataException ex)
            {
                setError($"Invalid data: {ex.Message}");
                return;
            }
            catch (TaskCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Logs.Error($"Internal error processing T2I request: {ex}");
                setError("Something went wrong while generating images.");
                return;
            }
            finally
            {
                claim.Complete(gens: 1, liveGens: 1);
                sendStatus();
            }
        }
    }
}
