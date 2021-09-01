// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

const path = require("path");
const webpack = require("./common/node_modules/webpack");
const TerserJsPlugin = require("./common/node_modules/terser-webpack-plugin");
const { DuplicatesPlugin } = require("./common/node_modules/inspectpack/plugin");

module.exports = function (modulePath, browserBaseName, options) {
    const pkg = require(path.resolve(modulePath, "package.json"));

    options = options || {};

    return {
        entry: path.resolve(modulePath, "src", "browser-index.ts"),
        mode: "none",
        node: {
            global: true
        },
        target: options.target,
        resolveLoader: {
            // Special resolution rules for loaders (which are in the 'common' directory)
            modules: [ path.resolve(__dirname, "common", "node_modules") ],
        },
        module: {
            rules: [
                {
                    test: /\.ts$/,
                    use: [
                        {
                            loader: "ts-loader",
                            options: {
                                configFile: path.resolve(modulePath, "tsconfig.json"),
                            },
                        },
                    ],
                    exclude: /node_modules/,
                }
            ]
        },
        resolve: {
            extensions: [".ts", ".js"],
            alias: {
                ...options.alias,
            }
        },
        output: {
            filename: `${browserBaseName}.js`,
            path: path.resolve(modulePath, "dist", options.platformDist || "browser"),
            library: {
                root: pkg.umd_name.split("."),
                amd: pkg.umd_name,
            },
            libraryTarget: "umd",
        },
        plugins: [
            new webpack.SourceMapDevToolPlugin({
                filename: `${browserBaseName}.js.map`,
                moduleFilenameTemplate(info) {
                    let resourcePath = info.resourcePath;

                    // Clean up the source map urls.
                    while (resourcePath.startsWith("./") || resourcePath.startsWith("../")) {
                        if (resourcePath.startsWith("./")) {
                            resourcePath = resourcePath.substring(2);
                        } else {
                            resourcePath = resourcePath.substring(3);
                        }
                    }

                    // We embed the sources so we can falsify the URLs a little, they just
                    // need to be identifiers that can be viewed in the browser.
                    return `webpack://${pkg.umd_name}/${resourcePath}`;
                }
            }),
            new DuplicatesPlugin({
                emitErrors: false,
                emitHandler: undefined,
                ignoredPackages: undefined,
                verbose: false
            })
        ],
        optimization: {
          sideEffects: true,
          concatenateModules: true,
          providedExports: true,
          usedExports: true,
          innerGraph: true,
          minimize: true,
          minimizer: [new TerserJsPlugin({
              terserOptions: {
                  ecma: 2019,
                  compress: {},
                  mangle: {
                    properties: {
                        regex: /^_/
                    }
                  },
                  module: true,
                  format: {
                      ecma: 2019
                  },
                  toplevel: false,
                  keep_classnames: false,
                  keep_fnames: false,
            }
          })]
        },
        stats: {
            warnings: true,
            errors: true,
            performance: true,
            optimizationBailout: true
        },
        externals: options.externals,
    };
}