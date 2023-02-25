﻿using DogScepterLib.Core.Models;
using DogScepterLib.Project.Assets;
using DogScepterLib.Project.Util;
using System.Drawing;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using static DogScepterLib.Project.Assets.AssetSprite.CollisionMaskInfo;
using System.Drawing.Imaging;
using DogScepterLib.Core;

namespace DogScepterLib.Project
{
    public static class CollisionMasks
    {
        public class Rect
        {
            public int Left, Top, Right, Bottom;

            public Rect(int left, int top, int right, int bottom)
            {
                Left = left;
                Top = top;
                Right = right;
                Bottom = bottom;
            }
        }

        private static unsafe byte GetHighestAlphaAt(List<DSImage> items, int x, int y)
        {
            byte highest = 0;

            foreach (var item in items)
            {
                fixed (byte* fixedPtr = &item.Data[0])
                {
                    byte alpha = *(fixedPtr + (x * 4) + (y * item.Width) + 3);
                    if (alpha > highest)
                        highest = alpha;
                }
            }

            return highest;
        }


        private static List<DSImage> GetImages(ProjectFile pf, int width, int height, IList<GMTextureItem> items)
        {
            List<DSImage> images = new(items.Count);

            // Make copies of all the entries for reference
            foreach (var item in items)
            {
                if (item == null)
                    continue;

                DSImage toAdd;
                if (item.TexturePageID != -1)
                    toAdd = pf.Textures.GetTextureEntryImage(item, width, height);
                else
                    toAdd = item._ImageBeforeCrop ?? item._Image;

                images.Add(toAdd);

                // Check to ensure every frame has the same dimensions
                if (toAdd.Width != width || toAdd.Height != height)
                    throw new Exception("Collision mask error: Sprite frame dimensions do not match.");
            }

            return images;
        }

        public unsafe static AssetSprite.CollisionMaskInfo GetInfoForSprite(ProjectFile pf, GMSprite spr, out List<DSImage> images, bool suggestPrecise = false)
        {
            images = new List<DSImage>(spr.TextureItems.Count);

            var info = new AssetSprite.CollisionMaskInfo
            {
                Mode = (MaskMode)spr.BBoxMode
            };

            if (spr.SepMasks == GMSprite.SepMaskType.AxisAlignedRect)
                info.Type = MaskType.Rectangle;
            else if (spr.SepMasks == GMSprite.SepMaskType.RotatedRect)
                info.Type = MaskType.RectangleWithRotation;

            // Some basic conditions to bail
            if (spr.CollisionMasks.Count != 1 && spr.CollisionMasks.Count != spr.TextureItems.Count)
                return info;
            if (spr.CollisionMasks.Count == 0)
                return info;

            // Get bitmaps from frames
            images = GetImages(pf, spr.Width, spr.Height, spr.TextureItems);

            int boundLeft = Math.Clamp(spr.MarginLeft, 0, spr.Width),
                boundRight = Math.Clamp(spr.MarginRight, 0, spr.Width - 1),
                boundTop = Math.Clamp(spr.MarginTop, 0, spr.Height),
                boundBottom = Math.Clamp(spr.MarginBottom, 0, spr.Height - 1);

            switch (spr.SepMasks)
            {
                case GMSprite.SepMaskType.AxisAlignedRect:
                case GMSprite.SepMaskType.RotatedRect:
                    switch (info.Mode)
                    {
                        case MaskMode.Automatic:
                            // Scan for the lowest alpha value in the bounding box
                            // When comparing each pixel, compare to the one in that spot with the highest alpha in every frame

                            bool foundNonzero = false;
                            byte lowest = 0;
                            byte highest = 0;

                            int stride = ((spr.Width + 7) / 8) * 8;

                            FastBitArray mask = new FastBitArray(spr.CollisionMasks[0].Memory.Span);
                            int strideFactor = boundTop * stride;

                            for (int y = boundTop; y <= boundBottom; y++)
                            {
                                for (int x = boundLeft; x <= boundRight; x++)
                                {
                                    if (mask.GetReverse(x + strideFactor))
                                    {
                                        byte highestAlpha = GetHighestAlphaAt(images, x, y);
                                        if (highestAlpha > highest)
                                            highest = highestAlpha;
                                        if (highestAlpha != 0 && (!foundNonzero || highestAlpha < lowest))
                                        {
                                            lowest = highestAlpha;
                                            foundNonzero = true;
                                        }
                                    }
                                }

                                strideFactor += stride;
                            }

                            if (foundNonzero)
                            {
                                if (lowest == highest)
                                    lowest = 0; // Could be anything
                                else
                                    --lowest;
                            }
                            info.AlphaTolerance = lowest;
                            break;
                        case MaskMode.Manual:
                            info.Left = spr.MarginLeft;
                            info.Right = spr.MarginRight;
                            info.Top = spr.MarginTop;
                            info.Bottom = spr.MarginBottom;
                            break;
                    }
                    break;
                case GMSprite.SepMaskType.Precise:
                    {
                        int stride = ((spr.Width + 7) / 8) * 8;

                        bool foundNonzero = false;
                        byte lowest = 0;
                        byte highest = 0;

                        if (spr.CollisionMasks.Count > 1 && spr.CollisionMasks.Count == spr.TextureItems.Count)
                        {
                            info.Type = MaskType.PrecisePerFrame;
                            
                            for (int i = 0; i < spr.CollisionMasks.Count; i++)
                            {
                                fixed (byte* fixedPtr = &images[i].Data[0])
                                {
                                    FastBitArray mask = new FastBitArray(spr.CollisionMasks[i].Memory.Span);
                                    int strideFactor = boundTop * stride;
                                    for (int y = boundTop; y <= boundBottom; y++)
                                    {
                                        for (int x = boundLeft; x <= boundRight; x++)
                                        {
                                            if (mask.GetReverse(x + strideFactor))
                                            {
                                                byte val = *(fixedPtr + (x * 4) + (y * images[i].Width) + 3);
                                                if (val > highest)
                                                    highest = val;
                                                if (val != 0 && (!foundNonzero || val < lowest))
                                                {
                                                    lowest = val;
                                                    foundNonzero = true;
                                                }
                                            }
                                        }

                                        strideFactor += stride;
                                    }
                                }
                            }
                        }
                        else
                        {
                            info.Type = MaskType.Precise;

                            // Scan for highest alpha, as well as diamond/ellipses
                            FastBitArray mask = new FastBitArray(spr.CollisionMasks[0].Memory.Span);

                            bool isDiamond = true, isEllipse = true;
                            float centerX = ((spr.MarginLeft + spr.MarginRight) / 2);
                            float centerY = ((spr.MarginTop + spr.MarginBottom) / 2);
                            float radiusX = centerX - spr.MarginLeft + 0.5f;
                            float radiusY = centerY - spr.MarginTop + 0.5f;

                            int strideFactor = boundTop * stride;

                            if (!suggestPrecise && radiusX > 0f && radiusY > 0f)
                            {
                                for (int y = boundTop; y <= boundBottom; y++)
                                {
                                    for (int x = boundLeft; x <= boundRight; x++)
                                    {
                                        float normalX = (x - centerX) / radiusX;
                                        float normalY = (y - centerY) / radiusY;
                                        bool inDiamond = Math.Abs(normalX) + Math.Abs(normalY) <= 1f;
                                        bool inEllipse = Math.Pow(normalX, 2.0d) + Math.Pow(normalY, 2.0d) <= 1.0d;

                                        if (mask.GetReverse(x + strideFactor))
                                        {
                                            isDiamond &= inDiamond;
                                            isEllipse &= inEllipse;

                                            byte highestAlpha = GetHighestAlphaAt(images, x, y);
                                            if (highestAlpha > highest)
                                                highest = highestAlpha;
                                            if (highestAlpha != 0 && (!foundNonzero || highestAlpha < lowest))
                                            {
                                                lowest = highestAlpha;
                                                foundNonzero = true;
                                            }
                                        }
                                        // Can't eliminate based on this, they can be split into pieces with multiple frames
                                        //else
                                        //{
                                        //    isDiamond &= !inDiamond;
                                        //    isEllipse &= !inEllipse;
                                        //}
                                    }

                                    strideFactor += stride;
                                }
                            }
                            else
                            {
                                // Version without diamond/ellipse checks
                                isDiamond = false;
                                isEllipse = false;

                                for (int y = boundTop; y <= boundBottom; y++)
                                {
                                    for (int x = boundLeft; x <= boundRight; x++)
                                    {
                                        if (mask.GetReverse(x + strideFactor))
                                        {
                                            byte highestAlpha = GetHighestAlphaAt(images, x, y);
                                            if (highestAlpha > highest)
                                                highest = highestAlpha;
                                            if (highestAlpha != 0 && (!foundNonzero || highestAlpha < lowest))
                                            {
                                                lowest = highestAlpha;
                                                foundNonzero = true;
                                            }
                                        }
                                    }

                                    strideFactor += stride;
                                }
                            }

                            if (isDiamond)
                                info.Type = MaskType.Diamond;
                            else if (isEllipse)
                                info.Type = MaskType.Ellipse;
                        }

                        if (info.Mode == MaskMode.Manual || 
                            (info.Mode == MaskMode.Automatic && info.Type != MaskType.Precise && info.Type != MaskType.PrecisePerFrame))
                        {
                            info.Left = spr.MarginLeft;
                            info.Right = spr.MarginRight;
                            info.Top = spr.MarginTop;
                            info.Bottom = spr.MarginBottom;
                        }

                        if (info.Mode == MaskMode.Automatic || info.Type == MaskType.Precise ||
                            (info.Mode == MaskMode.Manual && info.Type == MaskType.PrecisePerFrame))
                        {
                            if (foundNonzero)
                            {
                                if (lowest == highest)
                                    lowest = 0; // Could be anything
                                else
                                    --lowest;
                            }
                            info.AlphaTolerance = lowest;
                        }
                    }
                    break;
            }

            return info;
        }

        public static unsafe Rect GetBBoxForImage(DSImage img, AssetSprite spr)
        {
            var info = spr.CollisionMask;

            switch (info.Mode)
            {
                case MaskMode.Automatic:
                case MaskMode.RawAutomatic:
                    {
                        int left = spr.Width - 1, top = spr.Height - 1, right = 0, bottom = 0;

                        fixed (byte* fixedPtr = &img.Data[0])
                        {
                            byte* ptr = fixedPtr;
                            for (int y = 0; y < img.Height; y++)
                            {
                                for (int x = 0; x < img.Width; x++)
                                {
                                    if (*(ptr + 3) > info.AlphaTolerance)
                                    {
                                        if (x < left)
                                            left = x;
                                        if (y < top)
                                            top = y;
                                        if (x > right)
                                            right = x;
                                        if (y > bottom)
                                            bottom = y;
                                    }
                                    ptr += 4;
                                }
                            }
                        }

                        return new Rect(
                            Math.Max(0, left),
                            Math.Max(0, top),
                            Math.Min(spr.Width - 1, right),
                            Math.Min(spr.Height - 1, bottom));
                    }

                case MaskMode.FullImage:
                case MaskMode.RawFullImage:
                    return new Rect(0, 0, spr.Width - 1, spr.Height - 1);

                case MaskMode.Manual:
                case MaskMode.RawManual:
                    return new Rect(
                        Math.Clamp((int)info.Left, 0, spr.Width - 1),
                        Math.Clamp((int)info.Top, 0, spr.Height - 1),
                        Math.Clamp((int)info.Right, 0, spr.Width - 1),
                        Math.Clamp((int)info.Bottom, 0, spr.Height - 1));
            }

            throw new ArgumentException("invalid sprite mask mode");
        }

        public static unsafe FastBitArray GetMaskForImage(DSImage img, AssetSprite spr, ref Rect maskbbox, FastBitArray existingMask = null)
        {
            int stride = ((spr.Width + 7) / 8) * 8;
            FastBitArray res = existingMask ?? new FastBitArray(stride * spr.Height);

            Rect bbox = GetBBoxForImage(img, spr);

            var info = spr.CollisionMask;
            int sprLeft, sprTop, sprRight, sprBottom;

            // Word of note: There's a lot of copies of nearly the same code here. This is to reduce the number of conditions.

            int strideFactor = bbox.Top * stride;
            switch (info.Type)
            {
                case MaskType.Rectangle:
                case MaskType.RectangleWithRotation:
                    if (maskbbox != null)
                    {
                        for (int y = bbox.Top; y <= bbox.Bottom; y++)
                        {
                            for (int x = bbox.Left; x <= bbox.Right; x++)
                            {
                                res.SetTrueReverse(x + strideFactor);
                                if (x < maskbbox.Left)
                                    maskbbox.Left = x;
                                if (y < maskbbox.Top)
                                    maskbbox.Top = y;
                                if (x > maskbbox.Right)
                                    maskbbox.Right = x;
                                if (y > maskbbox.Bottom)
                                    maskbbox.Bottom = y;
                            }

                            strideFactor += stride;
                        }
                    }
                    else
                    {
                        for (int y = bbox.Top; y <= bbox.Bottom; y++)
                        {
                            for (int x = bbox.Left; x <= bbox.Right; x++)
                            {
                                res.SetTrueReverse(x + strideFactor);
                            }

                            strideFactor += stride;
                        }
                    }
                    break;
                case MaskType.Precise:
                case MaskType.PrecisePerFrame:
                    int tolerance = info.AlphaTolerance ?? 0;
                    fixed (byte* fixedPtr = &img.Data[0])
                    {
                        byte* ptr = fixedPtr;
                        if (maskbbox != null)
                        {
                            for (int y = bbox.Top; y <= bbox.Bottom; y++)
                            {
                                for (int x = bbox.Left; x <= bbox.Right; x++)
                                {
                                    if (*(ptr + (x * 4) + (y * img.Width) + 3) > tolerance)
                                    {
                                        res.SetTrueReverse(x + strideFactor);
                                        if (x < maskbbox.Left)
                                            maskbbox.Left = x;
                                        if (y < maskbbox.Top)
                                            maskbbox.Top = y;
                                        if (x > maskbbox.Right)
                                            maskbbox.Right = x;
                                        if (y > maskbbox.Bottom)
                                            maskbbox.Bottom = y;
                                    }
                                }

                                strideFactor += stride;
                            }
                        }
                        else
                        {
                            for (int y = bbox.Top; y <= bbox.Bottom; y++)
                            {
                                for (int x = bbox.Left; x <= bbox.Right; x++)
                                {
                                    if (*(ptr + (x * 4) + (y * img.Width) + 3) > tolerance)
                                        res.SetTrueReverse(x + strideFactor);
                                }

                                strideFactor += stride;
                            }
                        }
                    }
                    break;
                case MaskType.Diamond:
                    {
                        if (info.Mode == MaskMode.FullImage)
                        {
                            sprLeft = 0;
                            sprTop = 0;
                            sprRight = spr.Width - 1;
                            sprBottom = spr.Height - 1;
                        }
                        else
                        {
                            sprLeft = (int)info.Left;
                            sprTop = (int)info.Top;
                            sprRight = (int)info.Right;
                            sprBottom = (int)info.Bottom;
                        }

                        float centerX = (sprLeft + sprRight) / 2;
                        float centerY = (sprTop + sprBottom) / 2;
                        float radiusX = centerX - sprLeft + 0.5f;
                        float radiusY = centerY - sprTop + 0.5f;

                        if (radiusX <= 0 || radiusY <= 0)
                            break;

                        if (maskbbox != null)
                        {
                            for (int y = bbox.Top; y <= bbox.Bottom; y++)
                            {
                                for (int x = bbox.Left; x <= bbox.Right; x++)
                                {
                                    float normalX = (x - centerX) / radiusX;
                                    float normalY = (y - centerY) / radiusY;
                                    if (Math.Abs(normalX) + Math.Abs(normalY) <= 1f)
                                    {
                                        res.SetTrueReverse(x + strideFactor);
                                        if (x < maskbbox.Left)
                                            maskbbox.Left = x;
                                        if (y < maskbbox.Top)
                                            maskbbox.Top = y;
                                        if (x > maskbbox.Right)
                                            maskbbox.Right = x;
                                        if (y > maskbbox.Bottom)
                                            maskbbox.Bottom = y;
                                    }
                                }

                                strideFactor += stride;
                            }
                        }
                        else
                        {
                            for (int y = bbox.Top; y <= bbox.Bottom; y++)
                            {
                                for (int x = bbox.Left; x <= bbox.Right; x++)
                                {
                                    float normalX = (x - centerX) / radiusX;
                                    float normalY = (y - centerY) / radiusY;
                                    if (Math.Abs(normalX) + Math.Abs(normalY) <= 1f)
                                        res.SetTrueReverse(x + strideFactor);
                                }

                                strideFactor += stride;
                            }
                        }
                        break;
                    }
                case MaskType.Ellipse:
                    {
                        if (info.Mode == MaskMode.FullImage)
                        {
                            sprLeft = 0;
                            sprTop = 0;
                            sprRight = spr.Width - 1;
                            sprBottom = spr.Height - 1;
                        }
                        else
                        {
                            sprLeft = (int)info.Left;
                            sprTop = (int)info.Top;
                            sprRight = (int)info.Right;
                            sprBottom = (int)info.Bottom;
                        }

                        float centerX = (sprLeft + sprRight) / 2;
                        float centerY = (sprTop + sprBottom) / 2;
                        float radiusX = centerX - sprLeft + 0.5f;
                        float radiusY = centerY - sprTop + 0.5f;

                        if (radiusX <= 0 || radiusY <= 0)
                            break;

                        if (maskbbox != null)
                        {
                            for (int y = bbox.Top; y <= bbox.Bottom; y++)
                            {
                                for (int x = bbox.Left; x <= bbox.Right; x++)
                                {
                                    float normalX = (x - centerX) / radiusX;
                                    float normalY = (y - centerY) / radiusY;
                                    if (Math.Pow(normalX, 2.0d) + Math.Pow(normalY, 2.0d) <= 1.0d)
                                    {
                                        res.SetTrueReverse(x + strideFactor);
                                        if (x < maskbbox.Left)
                                            maskbbox.Left = x;
                                        if (y < maskbbox.Top)
                                            maskbbox.Top = y;
                                        if (x > maskbbox.Right)
                                            maskbbox.Right = x;
                                        if (y > maskbbox.Bottom)
                                            maskbbox.Bottom = y;
                                    }
                                }

                                strideFactor += stride;
                            }
                        }
                        else
                        {
                            for (int y = bbox.Top; y <= bbox.Bottom; y++)
                            {
                                for (int x = bbox.Left; x <= bbox.Right; x++)
                                {
                                    float normalX = (x - centerX) / radiusX;
                                    float normalY = (y - centerY) / radiusY;
                                    if (Math.Pow(normalX, 2.0d) + Math.Pow(normalY, 2.0d) <= 1.0d)
                                        res.SetTrueReverse(x + strideFactor);
                                }

                                strideFactor += stride;
                            }
                        }
                        break;
                    }
            }

            return res;
        }

        public static List<BufferRegion> GetMasksForSprite(ProjectFile pf, AssetSprite spr, out Rect maskbbox, List<DSImage> images = null)
        {
            if (images == null)
                images = GetImages(pf, spr.Width, spr.Height, spr.TextureItems);

            if (images.Count == 0)
            {
                maskbbox = new Rect(0, 0, 0, 0);
                return new List<BufferRegion>();
            }

            var info = spr.CollisionMask;

            if (info.Left == null || info.Top == null || info.Right == null || info.Bottom == null)
                maskbbox = new Rect(spr.Width - 1, spr.Height - 1, 0, 0);
            else
                maskbbox = null;

            if (spr.CollisionMask.Type == MaskType.PrecisePerFrame)
            {
                // Get masks for individual frames
                List<BufferRegion> res = new List<BufferRegion>(images.Count);
                for (int i = 0; i < images.Count; i++)
                    res.Add(new BufferRegion(GetMaskForImage(images[i], spr, ref maskbbox).ToByteArray()));

                if (maskbbox != null)
                {
                    maskbbox.Left = Math.Max(0, maskbbox.Left);
                    maskbbox.Top = Math.Max(0, maskbbox.Top);
                    maskbbox.Right = Math.Min(spr.Width - 1, maskbbox.Right);
                    maskbbox.Bottom = Math.Min(spr.Height - 1, maskbbox.Bottom);
                }
                return res;
            }
            else
            {
                // Get the mask for the first frame, then add following frames
                FastBitArray mask = GetMaskForImage(images[0], spr, ref maskbbox);
                for (int i = 1; i < images.Count; i++)
                    GetMaskForImage(images[i], spr, ref maskbbox, mask);

                if (maskbbox != null)
                {
                    maskbbox.Left = Math.Max(0, maskbbox.Left);
                    maskbbox.Top = Math.Max(0, maskbbox.Top);
                    maskbbox.Right = Math.Min(spr.Width - 1, maskbbox.Right);
                    maskbbox.Bottom = Math.Min(spr.Height - 1, maskbbox.Bottom);
                }
                return new List<BufferRegion> { new BufferRegion(mask.ToByteArray()) };
            }
        }

        public static unsafe bool CompareMasks(List<BufferRegion> a, List<BufferRegion> b)
        {
            if (a.Count != b.Count)
                return false;

            for (int i = 0; i < a.Count; i++)
            {
                ReadOnlySpan<byte> arrayA = a[i].Memory.Span;
                ReadOnlySpan<byte> arrayB = b[i].Memory.Span;

                if (arrayA.Length != arrayB.Length)
                    return false;
                if (arrayA.Length == 0)
                    continue;

                fixed (byte* ptrA = &arrayA[0])
                {
                    fixed (byte* ptrB = &arrayB[0])
                    {
                        byte* posA = ptrA;
                        byte* posB = ptrB;

                        for (int j = 0; j < arrayA.Length; j++)
                        {
                            if (*(posA++) != *(posB++))
                                return false;
                        }
                    }
                }
            }

            return true;
        }

        public static unsafe DSImage GetImageFromMask(int width, int height, ReadOnlySpan<byte> mask)
        {
            DSImage img = new(width, height);
            fixed (byte* bytePtr = &img.Data[0])
            {
                uint* ptrInt = (uint*)bytePtr;

                int stride = ((width + 7) / 8) * 8;
                int strideFactor = 0;

                FastBitArray arr = new(mask);
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (arr.GetReverse(x + strideFactor))
                        {
                            *(ptrInt + x + (y * width)) = 0xFFFFFFFFu;
                        }
                        else
                        {
                            *(ptrInt + x + (y * width)) = 0xFF000000u;
                        }
                    }

                    strideFactor += stride;
                }
            }
            return img;
        }

        public static unsafe byte[] GetMaskFromImage(DSImage img)
        {
            int stride = ((img.Width + 7) / 8) * 8;
            FastBitArray arr = new FastBitArray(stride * img.Height);
            int strideFactor = 0;

            fixed (byte* bytePtr = &img.Data[0])
            {
                uint* ptrInt = (uint*)bytePtr;

                for (int y = 0; y < img.Height; y++)
                {
                    for (int x = 0; x < img.Width; x++)
                    {
                        if (*(ptrInt + x + (y * img.Width)) == 0xFFFFFFFFu)
                        {
                            arr.SetTrueReverse(x + strideFactor);
                        }
                    }

                    strideFactor += stride;
                }
            }

            return arr.ToByteArray();
        }
    }
}
