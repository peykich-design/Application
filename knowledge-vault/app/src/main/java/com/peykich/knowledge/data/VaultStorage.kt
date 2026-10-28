package com.peykich.knowledge.data

import java.io.File
import java.io.IOException

/**
 * ローカル Vault（filesDir/vault/）のファイル操作。パスは Vault ルートからの相対パス（区切り「/」）。
 * 書き込みは一時ファイルに書いてからリネームする（途中で落ちても元のファイルが壊れない）。
 */
class VaultStorage(val root: File) {

    fun file(path: String): File {
        val f = File(root, path)
        // 「..」などで Vault の外に出ないようにする
        if (!f.canonicalPath.startsWith(root.canonicalPath + File.separator)) throw IOException("Vault の外のパス: $path")
        return f
    }

    fun exists(path: String): Boolean = file(path).exists()

    fun read(path: String): String? = file(path).takeIf { it.isFile }?.readText(Charsets.UTF_8)

    fun write(path: String, content: String) {
        val target = file(path)
        target.parentFile?.mkdirs()
        val tmp = File(target.parentFile, ".${target.name}.tmp")
        tmp.writeText(content, Charsets.UTF_8)
        if (!tmp.renameTo(target)) {
            target.delete()
            if (!tmp.renameTo(target)) {
                tmp.delete()
                throw IOException("保存できませんでした: $path")
            }
        }
    }

    /** 内容が同じなら書かない（index.md の無駄な更新を避ける）。書いたら true。 */
    fun writeIfChanged(path: String, content: String): Boolean {
        if (read(path) == content) return false
        write(path, content)
        return true
    }

    /** Vault 内の md ファイルの相対パス一覧（「.」で始まるものは除く）。 */
    fun listMarkdown(): List<String> {
        if (!root.exists()) return emptyList()
        val base = root.canonicalFile
        return base.walkTopDown()
            .onEnter { it == base || !it.name.startsWith(".") }
            .filter { it.isFile && it.name.endsWith(".md") && !it.name.startsWith(".") }
            .map { it.relativeTo(base).invariantSeparatorsPath }
            .toList()
    }
}
