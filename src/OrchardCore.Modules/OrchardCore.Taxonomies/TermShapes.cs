using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using OrchardCore.ContentManagement;
using OrchardCore.ContentManagement.Metadata;
using OrchardCore.DisplayManagement;
using OrchardCore.DisplayManagement.Descriptors;
using OrchardCore.Mvc.Utilities;
using OrchardCore.Taxonomies.Models;

namespace OrchardCore.Taxonomies
{
    public class TermShapes : IShapeTableProvider
    {
        public void Discover(ShapeTableBuilder builder)
        {
            // Add standard alternates to a TermPart because it is rendered by a content display driver not a part display driver
            builder.Describe("TermPart")
                .OnDisplaying(context =>
                {
                    dynamic shape = context.Shape;

                    var contentType = shape.ContentItem.ContentType;
                    var displayTypes = new[] { "", "_" + context.Shape.Metadata.DisplayType };

                    // [ShapeType]_[DisplayType], e.g. TermPart.Summary, TermPart.Detail
                    context.Shape.Metadata.Alternates.Add($"TermPart_{context.Shape.Metadata.DisplayType}");

                    foreach (var displayType in displayTypes)
                    {
                        // [ContentType]_[DisplayType]__[PartType], e.g. Category-TermPart, Category-TermPart.Detail
                        context.Shape.Metadata.Alternates.Add($"{contentType}{displayType}__TermPart");
                    }
                });

            builder.Describe("Term")
                .OnProcessing(async context =>
                {
                    dynamic termShape = context.Shape;
                    string identifier = termShape.TaxonomyContentItemId ?? termShape.Alias;

                    if (String.IsNullOrEmpty(identifier))
                    {
                        return;
                    }

                    termShape.Classes.Add("term");

                    // Term population is executed when processing the shape so that its value
                    // can be cached. IShapeDisplayEvents is called before the ShapeDescriptor
                    // events and thus this code can be cached.

                    var shapeFactory = context.ServiceProvider.GetRequiredService<IShapeFactory>();
                    var contentManager = context.ServiceProvider.GetRequiredService<IContentManager>();
                    var aliasManager = context.ServiceProvider.GetRequiredService<IContentAliasManager>();
                    var orchardHelper = context.ServiceProvider.GetRequiredService<IOrchardHelper>();
                    var contentDefinitionManager = context.ServiceProvider.GetRequiredService<IContentDefinitionManager>();

                    string taxonomyContentItemId = termShape.Alias != null
                        ? await aliasManager.GetContentItemIdAsync(termShape.Alias)
                        : termShape.TaxonomyContentItemId;

                    if (taxonomyContentItemId == null)
                    {
                        return;
                    }

                    var taxonomyContentItem = await contentManager.GetAsync(taxonomyContentItemId);

                    if (taxonomyContentItem == null)
                    {
                        return;
                    }

                    termShape.TaxonomyContentItem = taxonomyContentItem;

                    var taxonomyPart = taxonomyContentItem.As<TaxonomyPart>();
                    if (taxonomyPart == null)
                    {
                        return;
                    }

                    // When a TermContentItemId is provided render the inherited heirachy inside the taxonomy.
                    List<ContentItem> termItems = null;
                    string termContentItemId = termShape.TermContentItemId;
                    if (!String.IsNullOrEmpty(termContentItemId))
                    {
                        var termContentItem = await orchardHelper.GetTaxonomyTermAsync(taxonomyContentItem.ContentItemId, termContentItemId);
                        if (termContentItem == null)
                        {
                            return;
                        }
                        termShape.TermName = taxonomyContentItem.DisplayText;

                        termItems = await orchardHelper.GetInheritedTermsAsync(taxonomyContentItem.ContentItemId, termContentItemId);
                    }
                    else
                    {
                        termItems = taxonomyPart.Terms;
                        termShape.TermName = taxonomyContentItem.DisplayText;
                    }

                    if (termItems == null)
                    {
                        return;
                    }

                    string differentiator = FormatName((string)termShape.TermName);

                    if (!String.IsNullOrEmpty(differentiator))
                    {
                        // Term__[TermName] e.g. Term-Categories, Term-Travel
                        termShape.Metadata.Alternates.Add("Term__" + differentiator);
                        termShape.Differentiator = differentiator;
                        termShape.Classes.Add(("term-" + differentiator).HtmlClassify());
                    }

                    termShape.Classes.Add(("term-" + taxonomyPart.TermContentType).HtmlClassify());

                    var encodedContentType = EncodeAlternateElement(taxonomyPart.TermContentType);
                    // Term__[TaxonomyContentType] e.g. Term-Category, Term-Tag
                    termShape.Metadata.Alternates.Add("Term__" + encodedContentType);

                    // The first level of term item shapes is created.
                    // Each other level is created when the term item is displayed.

                    foreach (var termContentItem in termItems)
                    {
                        ContentItem[] childTerms = null;
                        if (termContentItem.Content.Terms is JArray termsArray)
                        {
                            childTerms = termsArray.ToObject<ContentItem[]>();
                        }

                        var shape = await shapeFactory.CreateAsync("TermItem", Arguments.From(new
                        {
                            Level = 0,
                            TermContentItem = termContentItem,
                            Terms = childTerms ?? Array.Empty<ContentItem>(),
                            TaxonomyContentItem = taxonomyContentItem,
                            Differentiator = differentiator
                        }));

                        // Don't use Items.Add() or the collection won't be sorted
                        termShape.Add(shape);
                    }
                });

            builder.Describe("TermItem")
                .OnDisplaying(async context =>
                {
                    dynamic termItem = context.Shape;
                    int level = termItem.Level;
                    ContentItem taxonomyContentItem = termItem.TaxonomyContentItem;
                    var taxonomyPart = taxonomyContentItem.As<TaxonomyPart>();
                    string differentiator = termItem.Differentiator;

                    var shapeFactory = context.ServiceProvider.GetRequiredService<IShapeFactory>();

                    if (termItem.Terms != null)
                    {
                        foreach (var termContentItem in termItem.Terms)
                        {
                            ContentItem[] childTerms = null;
                            if (termContentItem.Content.Terms is JArray termsArray)
                            {
                                childTerms = termsArray.ToObject<ContentItem[]>();
                            }
                            var shape = await shapeFactory.CreateAsync("TermItem", Arguments.From(new
                            {
                                Level = level + 1,
                                TaxonomyContentItem = taxonomyContentItem,
                                Differentiator = differentiator,
                                TermContentItem = termContentItem,
                                Terms = childTerms ?? Array.Empty<ContentItem>()
                            }));

                            // Don't use Items.Add() or the collection won't be sorted
                            termItem.Add(shape);
                        }
                    }

                    var encodedContentType = EncodeAlternateElement(taxonomyPart.TermContentType);

                    // TermItem__level__[level] e.g. TermItem-level-2
                    termItem.Metadata.Alternates.Add("TermItem__level__" + level);

                    // TermItem__[ContentType] e.g. TermItem-Category
                    // TermItem__[ContentType]__level__[level] e.g. TermItem-Category-level-2
                    termItem.Metadata.Alternates.Add("TermItem__" + encodedContentType);
                    termItem.Metadata.Alternates.Add("TermItem__" + encodedContentType + "__level__" + level);

                    if (!String.IsNullOrEmpty(differentiator))
                    {
                        // TermItem__[TermName] e.g. TermItem-Categories, TermItem-Travel
                        // TermItem__[TermName]__level__[level] e.g. TermItem-Categories-level-2
                        // TermItem__[TermName]__level__[level] e.g. TermItem-Travel-level-1
                        termItem.Metadata.Alternates.Add("TermItem__" + differentiator);
                        termItem.Metadata.Alternates.Add("TermItem__" + differentiator + "__level__" + level);

                        // TermItem__[TermName]__[ContentType] e.g. TermItem-Categories-Category, TermItem-Travel-Category
                        // TermItem__[TermName]__[ContentType]__level__[level] e.g. TermItem-Categories-Category-level-2
                        // TermItem__[TermName]__[ContentType]__level__[level] e.g. TermItem-Travel-Category-level-1
                        termItem.Metadata.Alternates.Add("TermItem__" + differentiator + "__" + encodedContentType);
                        termItem.Metadata.Alternates.Add("TermItem__" + differentiator + "__" + encodedContentType + "__level__" + level);
                    }
                });

            builder.Describe("TermContentItem")
                .OnDisplaying(displaying =>
                {
                    dynamic termItem = displaying.Shape;
                    int level = termItem.Level;
                    string differentiator = termItem.Differentiator;

                    ContentItem termContentItem = termItem.TermContentItem;

                    var encodedContentType = EncodeAlternateElement(termContentItem.ContentItem.ContentType);

                    termItem.Metadata.Alternates.Add("TermContentItem__level__" + level);

                    // TermContentItem__[ContentType] e.g. TermContentItem-Category
                    // TermContentItem__[ContentType]__level__[level] e.g. TermContentItem-Category-level-2
                    termItem.Metadata.Alternates.Add("TermContentItem__" + encodedContentType);
                    termItem.Metadata.Alternates.Add("TermContentItem__" + encodedContentType + "__level__" + level);

                    if (!String.IsNullOrEmpty(differentiator))
                    {
                        // TermContentItem__[TermName] e.g. The taxonomy root : TermContentItem-Categories
                        // TermContentItem__[TermName] e.g. The term : TermContentItem-Travel
                        termItem.Metadata.Alternates.Add("TermContentItem__" + differentiator);
                        // TermContentItem__[TermName]__level__[level] e.g. TermContentItem-Categories-level-2
                        // TermContentItem__[TermName]__level__[level] e.g. TermContentItem-Travel-level-1
                        termItem.Metadata.Alternates.Add("TermContentItem__" + differentiator + "__level__" + level);

                        // TermContentItem__[TermName]__[ContentType] e.g. TermContentItem-Categories-Category
                        // TermContentItem__[TermName]__[ContentType] e.g. TermContentItem-Travel-Category
                        // TermContentItem__[TermName]__[ContentType] e.g. TermContentItem-Categories-Category-level-2
                        // TermContentItem__[TermName]__[ContentType] e.g. TermContentItem-Travel-Category-level-1
                        termItem.Metadata.Alternates.Add("TermContentItem__" + differentiator + "__" + encodedContentType);
                        termItem.Metadata.Alternates.Add("TermContentItem__" + differentiator + "__" + encodedContentType + "__level__" + level);
                    }
                });
        }

        /// <summary>
        /// Encodes dashed and dots so that they don't conflict in filenames
        /// </summary>
        /// <param name="alternateElement"></param>
        /// <returns></returns>
        private string EncodeAlternateElement(string alternateElement)
        {
            return alternateElement.Replace("-", "__").Replace('.', '_');
        }

        /// <summary>
        /// Converts "foo-ba r" to "FooBaR"
        /// </summary>
        private static string FormatName(string name)
        {
            if (String.IsNullOrEmpty(name))
            {
                return null;
            }

            name = name.Trim();
            var nextIsUpper = true;
            var result = new StringBuilder(name.Length);
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];

                if (c == '-' || char.IsWhiteSpace(c))
                {
                    nextIsUpper = true;
                    continue;
                }

                if (nextIsUpper)
                {
                    result.Append(c.ToString().ToUpper());
                }
                else
                {
                    result.Append(c);
                }

                nextIsUpper = false;
            }

            return result.ToString();
        }
    }
}