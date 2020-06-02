using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ContentPatcher.Framework.Conditions;
using ContentPatcher.Framework.ConfigModels;
using ContentPatcher.Framework.Tokens;
using Microsoft.Xna.Framework;
using Pathoschild.Stardew.Common.Utilities;
using StardewModdingAPI;

namespace ContentPatcher.Framework.Patches
{
    /// <summary>Metadata for a conditional patch.</summary>
    internal abstract class Patch : IPatch
    {
        /*********
        ** Fields
        *********/
        /// <summary>Normalize an asset name.</summary>
        private readonly Func<string, string> NormalizeAssetNameImpl;

        /// <summary>The underlying contextual values.</summary>
        protected readonly AggregateContextual Contextuals = new AggregateContextual();

        /// <summary>The tokens that are updated manually, rather than via <see cref="Contextuals"/>.</summary>
        private readonly HashSet<IContextual> ManuallyUpdatedTokens = new HashSet<IContextual>(new ObjectReferenceComparer<IContextual>());

        /// <summary>Diagnostic info about the instance.</summary>
        protected readonly ContextualState State = new ContextualState();

        /// <summary>The context which provides tokens specific to this patch like <see cref="ConditionType.Target"/>.</summary>
        private readonly LocalContext PrivateContext;

        /// <summary>Whether the <see cref="FromAsset"/> file exists.</summary>
        private bool FromAssetExistsImpl;

        /// <summary>The <see cref="RawFromAsset"/> with support for managing its state.</summary>
        private IManagedTokenString ManagedRawFromAsset { get; }

        /// <summary>The <see cref="RawTargetAsset"/> with support for managing its state.</summary>
        protected IManagedTokenString ManagedRawTargetAsset { get; }


        /*********
        ** Accessors
        *********/
        /// <summary>A unique name for this patch shown in log messages.</summary>
        public string LogName { get; }

        /// <summary>The patch type.</summary>
        public PatchType Type { get; }

        /// <summary>The content pack which requested the patch.</summary>
        public ManagedContentPack ContentPack { get; }

        /// <summary>Whether the instance may change depending on the context.</summary>
        public bool IsMutable { get; } = true;

        /// <summary>Whether the instance is valid for the current context.</summary>
        public bool IsReady { get; protected set; }

        /// <summary>The normalized asset key from which to load the local asset (if applicable).</summary>
        public string FromAsset { get; private set; }

        /// <summary>The raw asset key from which to load the local asset (if applicable), including tokens.</summary>
        public ITokenString RawFromAsset => this.ManagedRawFromAsset;

        /// <summary>The normalized asset name to intercept.</summary>
        public string TargetAsset { get; private set; }

        /// <summary>The raw asset name to intercept, including tokens.</summary>
        public ITokenString RawTargetAsset => this.ManagedRawTargetAsset;

        /// <summary>The conditions which determine whether this patch should be applied.</summary>
        public Condition[] Conditions { get; }

        /// <summary>Whether the patch is currently applied to the target asset.</summary>
        public bool IsApplied { get; set; }


        /*********
        ** Public methods
        *********/
        /// <summary>Update the patch data when the context changes.</summary>
        /// <param name="context">Provides access to contextual tokens.</param>
        /// <returns>Returns whether the patch data changed.</returns>
        public virtual bool UpdateContext(IContext context)
        {
            // reset
            bool wasReady = this.IsReady;
            this.State.Reset();
            bool isReady = true;

            // update local tokens
            // (FromFile and Target may reference each other, so they need to be updated in a
            // specific order. A circular reference isn't possible since that's checked when the
            // patch is loaded.)
            this.PrivateContext.Update(context);
            bool changed = false;
            if (this.ManagedRawTargetAsset.UsesTokens(ConditionType.FromFile))
                changed |= this.UpdateFromFile(this.PrivateContext) | this.UpdateTargetPath(this.PrivateContext);
            else
                changed |= this.UpdateTargetPath(this.PrivateContext) | this.UpdateFromFile(this.PrivateContext);
            isReady &= this.RawTargetAsset.IsReady && this.RawFromAsset?.IsReady != false;

            // update contextuals
            changed |= this.Contextuals.UpdateContext(this.PrivateContext, except: this.ManuallyUpdatedTokens);
            isReady &= this.Contextuals.IsReady && (!this.Conditions.Any() || this.Conditions.All(p => p.IsMatch));
            this.FromAssetExistsImpl = false;

            // check from asset existence
            if (isReady && this.FromAsset != null)
            {
                this.FromAssetExistsImpl = this.ContentPack.HasFile(this.FromAsset);
                if (!this.FromAssetExistsImpl && this.Conditions.All(p => p.IsMatch))
                    this.State.AddErrors($"{nameof(PatchConfig.FromFile)} '{this.FromAsset}' does not exist");
            }

            // update
            this.IsReady = isReady;
            return changed || this.IsReady != wasReady;
        }

        /// <summary>Get whether the <see cref="FromAsset"/> file exists.</summary>
        public bool FromAssetExists()
        {
            return this.FromAssetExistsImpl;
        }

        /// <summary>Load the initial version of the asset.</summary>
        /// <typeparam name="T">The asset type.</typeparam>
        /// <param name="asset">The asset to load.</param>
        /// <exception cref="NotSupportedException">The current patch type doesn't support loading assets.</exception>
        public virtual T Load<T>(IAssetInfo asset)
        {
            throw new NotSupportedException("This patch type doesn't support loading assets.");
        }

        /// <summary>Apply the patch to a loaded asset.</summary>
        /// <typeparam name="T">The asset type.</typeparam>
        /// <param name="asset">The asset to edit.</param>
        /// <exception cref="NotSupportedException">The current patch type doesn't support editing assets.</exception>
        public virtual void Edit<T>(IAssetData asset)
        {
            throw new NotSupportedException("This patch type doesn't support loading assets.");
        }

        /// <summary>Get the token names used by this patch in its fields.</summary>
        public virtual IEnumerable<string> GetTokensUsed()
        {
            return this.Contextuals.GetTokensUsed();
        }

        /// <summary>Get diagnostic info about the contextual instance.</summary>
        public IContextualState GetDiagnosticState()
        {
            return this.State.Clone()
                .MergeFrom(this.Contextuals.GetDiagnosticState());
        }

        /// <summary>Get a human-readable list of changes applied to the asset for display when troubleshooting.</summary>
        public abstract IEnumerable<string> GetChangeLabels();


        /*********
        ** Protected methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="logName">A unique name for this patch shown in log messages.</param>
        /// <param name="type">The patch type.</param>
        /// <param name="contentPack">The content pack which requested the patch.</param>
        /// <param name="assetName">The normalized asset name to intercept.</param>
        /// <param name="conditions">The conditions which determine whether this patch should be applied.</param>
        /// <param name="normalizeAssetName">Normalize an asset name.</param>
        /// <param name="fromAsset">The normalized asset key from which to load the local asset (if applicable), including tokens.</param>
        protected Patch(string logName, PatchType type, ManagedContentPack contentPack, IManagedTokenString assetName, IEnumerable<Condition> conditions, Func<string, string> normalizeAssetName, IManagedTokenString fromAsset = null)
        {
            this.LogName = logName;
            this.Type = type;
            this.ContentPack = contentPack;
            this.ManagedRawTargetAsset = assetName;
            this.Conditions = conditions.ToArray();
            this.NormalizeAssetNameImpl = normalizeAssetName;
            this.PrivateContext = new LocalContext(scope: this.ContentPack.Manifest.UniqueID);
            this.ManagedRawFromAsset = fromAsset;

            this.Contextuals
                .Add(this.Conditions)
                .Add(assetName)
                .Add(fromAsset);
            this.ManuallyUpdatedTokens.Add(assetName);
            this.ManuallyUpdatedTokens.Add(fromAsset);
        }

        /// <summary>Try to read a tokenized rectangle.</summary>
        /// <param name="tokenArea">The tokenized rectangle to parse.</param>
        /// <param name="defaultX">The X value if the input area is null.</param>
        /// <param name="defaultY">The Y value if the input area is null.</param>
        /// <param name="defaultWidth">The width if the input area is null.</param>
        /// <param name="defaultHeight">The height if the input area is null.</param>
        /// <param name="area">The parsed rectangle.</param>
        /// <param name="error">The error phrase indicating why parsing failed, if applicable.</param>
        /// <returns>Returns whether the rectangle was successfully parsed.</returns>
        protected bool TryReadArea(TokenRectangle tokenArea, int defaultX, int defaultY, int defaultWidth, int defaultHeight, out Rectangle area, out string error)
        {
            if (tokenArea != null)
                return tokenArea.TryGetRectangle(out area, out error);

            area = new Rectangle(defaultX, defaultY, defaultWidth, defaultHeight);
            error = null;
            return true;
        }

        /// <summary>Update the target path, and add the relevant tokens to the patch context.</summary>
        /// <param name="context">The local patch context (already updated from the parent context).</param>
        /// <returns>Returns whether the field changed.</returns>
        private bool UpdateTargetPath(LocalContext context)
        {
            bool changed = this.ManagedRawTargetAsset.UpdateContext(context);

            if (this.RawTargetAsset.IsReady)
            {
                this.TargetAsset = this.NormalizeAssetNameImpl(this.RawTargetAsset.Value);
                context.SetLocalValue(ConditionType.Target.ToString(), this.TargetAsset);
                context.SetLocalValue(ConditionType.TargetWithoutPath.ToString(), Path.GetFileName(this.TargetAsset));
            }
            else
                this.TargetAsset = "";

            return changed;
        }

        /// <summary>Update the 'FromFile' value, and add the relevant tokens to the patch context.</summary>
        /// <param name="context">The local patch context (already updated from the parent context).</param>
        /// <returns>Returns whether the field changed.</returns>
        private bool UpdateFromFile(LocalContext context)
        {
            // no value
            if (this.ManagedRawFromAsset == null)
            {
                this.FromAsset = null;
                context.SetLocalValue(ConditionType.FromFile.ToString(), "");
                return false;
            }

            // update
            bool changed = this.ManagedRawFromAsset.UpdateContext(context);
            if (this.RawFromAsset.IsReady)
            {
                this.FromAsset = this.NormalizeLocalAssetPath(this.RawFromAsset.Value, logName: $"{nameof(PatchConfig.FromFile)} field");
                context.SetLocalValue(ConditionType.FromFile.ToString(), this.FromAsset);
            }
            else
                this.FromAsset = null;
            return changed;
        }

        /// <summary>Get a normalized file path relative to the content pack folder.</summary>
        /// <param name="path">The relative asset path.</param>
        /// <param name="logName">A descriptive name for the field being normalized shown in error messages.</param>
        private string NormalizeLocalAssetPath(string path, string logName)
        {
            try
            {
                // normalize asset name
                if (string.IsNullOrWhiteSpace(path))
                    return null;
                string newPath = this.NormalizeAssetNameImpl(path);

                // add .xnb extension if needed (it's stripped from asset names)
                string fullPath = this.ContentPack.GetFullPath(newPath);
                if (!File.Exists(fullPath))
                {
                    if (File.Exists($"{fullPath}.xnb") || Path.GetExtension(path) == ".xnb")
                        newPath += ".xnb";
                }

                return newPath;
            }
            catch (Exception ex)
            {
                throw new FormatException($"The {logName} for patch '{this.LogName}' isn't a valid asset path (current value: '{path}').", ex);
            }
        }
    }
}
