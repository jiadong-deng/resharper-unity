package com.jetbrains.resharper.plugins.unity.ideaInterop.fileTypes.shaderLab

import com.jetbrains.resharper.ideaInterop.fileTypes.RiderLanguageFileTypeBase
import com.jetbrains.resharper.plugins.unity.util.UnityIcons

object ShaderLabFileType : RiderLanguageFileTypeBase(ShaderLabLanguage) {
    override fun getDefaultExtension() = "shader"
    override fun getDescription() = "ShaderLab file"
    override fun getIcon() = UnityIcons.ShaderLabFile
    override fun getName() = "ShaderLab"
}

