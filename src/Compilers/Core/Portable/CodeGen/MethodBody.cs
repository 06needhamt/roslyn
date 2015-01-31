// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Generic;
using System;
using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis.Emit;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CodeGen
{
    /// <summary>
    /// Holds on to the method body data.
    /// </summary>
    internal sealed class MethodBody : Cci.IMethodBody
    {
        private readonly Cci.IMethodDefinition _parent;

        private readonly byte[] _ilBits;
        private readonly ushort _maxStack;
        private readonly ImmutableArray<Cci.ILocalDefinition> _locals;
        private readonly ImmutableArray<Cci.ExceptionHandlerRegion> _exceptionHandlers;

        // Debug information emitted to Release & Debug PDBs supporting the debugger, EEs and other tools:
        private readonly SequencePointList _sequencePoints;
        private readonly DebugDocumentProvider _debugDocumentProvider;
        private readonly ImmutableArray<Cci.LocalScope> _localScopes;
        private readonly ImmutableArray<Cci.NamespaceScope> _namespaceScopes;
        private readonly string _stateMachineTypeNameOpt;
        private readonly ImmutableArray<Cci.StateMachineHoistedLocalScope> _stateMachineHoistedLocalScopes;
        private readonly Cci.NamespaceScopeEncoding _namespaceScopeEncoding;
        private readonly bool _hasDynamicLocalVariables;
        private readonly Cci.AsyncMethodBodyDebugInfo _asyncMethodDebugInfo;

        // Debug information emitted to Debug PDBs supporting EnC:
        private readonly int _methodOrdinal;
        private readonly ImmutableArray<EncHoistedLocalInfo> _stateMachineHoistedLocalSlots;
        private readonly ImmutableArray<LambdaDebugInfo> _lambdaDebugInfo;
        private readonly ImmutableArray<ClosureDebugInfo> _closureDebugInfo;

        // Data used when emitting EnC delta:
        private readonly ImmutableArray<Cci.ITypeReference> _stateMachineAwaiterSlots;

        public MethodBody(
            byte[] ilBits,
            ushort maxStack,
            Cci.IMethodDefinition parent,
            int methodOrdinal,
            ImmutableArray<Cci.ILocalDefinition> locals,
            SequencePointList sequencePoints,
            DebugDocumentProvider debugDocumentProvider,
            ImmutableArray<Cci.ExceptionHandlerRegion> exceptionHandlers,
            ImmutableArray<Cci.LocalScope> localScopes,
            bool hasDynamicLocalVariables,
            ImmutableArray<Cci.NamespaceScope> namespaceScopes,
            Cci.NamespaceScopeEncoding namespaceScopeEncoding,
            ImmutableArray<LambdaDebugInfo> lambdaDebugInfo,
            ImmutableArray<ClosureDebugInfo> closureDebugInfo,
            string stateMachineTypeNameOpt,
            ImmutableArray<Cci.StateMachineHoistedLocalScope> stateMachineHoistedLocalScopes,
            ImmutableArray<EncHoistedLocalInfo> stateMachineHoistedLocalSlots,
            ImmutableArray<Cci.ITypeReference> stateMachineAwaiterSlots,
            Cci.AsyncMethodBodyDebugInfo asyncMethodDebugInfo)
        {
            Debug.Assert(!locals.IsDefault);
            Debug.Assert(!exceptionHandlers.IsDefault);
            Debug.Assert(!localScopes.IsDefault);

            _ilBits = ilBits;
            _asyncMethodDebugInfo = asyncMethodDebugInfo;
            _maxStack = maxStack;
            _parent = parent;
            _methodOrdinal = methodOrdinal;
            _locals = locals;
            _sequencePoints = sequencePoints;
            _debugDocumentProvider = debugDocumentProvider;
            _exceptionHandlers = exceptionHandlers;
            _localScopes = localScopes;
            _namespaceScopeEncoding = namespaceScopeEncoding;
            _hasDynamicLocalVariables = hasDynamicLocalVariables;
            _namespaceScopes = namespaceScopes.IsDefault ? ImmutableArray<Cci.NamespaceScope>.Empty : namespaceScopes;
            _lambdaDebugInfo = lambdaDebugInfo;
            _closureDebugInfo = closureDebugInfo;
            _stateMachineTypeNameOpt = stateMachineTypeNameOpt;
            _stateMachineHoistedLocalScopes = stateMachineHoistedLocalScopes;
            _stateMachineHoistedLocalSlots = stateMachineHoistedLocalSlots;
            _stateMachineAwaiterSlots = stateMachineAwaiterSlots;
        }

        void Cci.IMethodBody.Dispatch(Cci.MetadataVisitor visitor)
        {
            throw ExceptionUtilities.Unreachable;
        }

        ImmutableArray<Cci.ExceptionHandlerRegion> Cci.IMethodBody.ExceptionRegions
        {
            get { return _exceptionHandlers; }
        }

        bool Cci.IMethodBody.LocalsAreZeroed
        {
            get { return true; }
        }

        ImmutableArray<Cci.ILocalDefinition> Cci.IMethodBody.LocalVariables
        {
            get { return _locals; }
        }

        Cci.IMethodDefinition Cci.IMethodBody.MethodDefinition
        {
            get { return _parent; }
        }

        Cci.AsyncMethodBodyDebugInfo Cci.IMethodBody.AsyncDebugInfo
        {
            get { return _asyncMethodDebugInfo; }
        }

        ushort Cci.IMethodBody.MaxStack
        {
            get { return _maxStack; }
        }

        public byte[] IL
        {
            get { return _ilBits; }
        }

        public ImmutableArray<Cci.SequencePoint> GetSequencePoints()
        {
            return HasAnySequencePoints ?
                _sequencePoints.GetSequencePoints(_debugDocumentProvider) :
                ImmutableArray<Cci.SequencePoint>.Empty;
        }

        public bool HasAnySequencePoints
        {
            get
            {
                return _sequencePoints != null && !_sequencePoints.IsEmpty;
            }
        }

        public ImmutableArray<Cci.SequencePoint> GetLocations()
        {
            return GetSequencePoints();
        }

        ImmutableArray<Cci.LocalScope> Cci.IMethodBody.LocalScopes
        {
            get
            {
                return _localScopes;
            }
        }

        /// <summary>
        /// This is a list of the using directives that were in scope for this method body.
        /// </summary>
        ImmutableArray<Cci.NamespaceScope> Cci.IMethodBody.NamespaceScopes
        {
            get
            {
                return _namespaceScopes;
            }
        }

        string Cci.IMethodBody.StateMachineTypeName
        {
            get
            {
                return _stateMachineTypeNameOpt;
            }
        }

        ImmutableArray<Cci.StateMachineHoistedLocalScope> Cci.IMethodBody.StateMachineHoistedLocalScopes
        {
            get
            {
                return _stateMachineHoistedLocalScopes;
            }
        }

        ImmutableArray<EncHoistedLocalInfo> Cci.IMethodBody.StateMachineHoistedLocalSlots
        {
            get
            {
                return _stateMachineHoistedLocalSlots;
            }
        }

        ImmutableArray<Cci.ITypeReference> Cci.IMethodBody.StateMachineAwaiterSlots
        {
            get
            {
                return _stateMachineAwaiterSlots;
            }
        }

        public Cci.NamespaceScopeEncoding NamespaceScopeEncoding
        {
            get
            {
                return _namespaceScopeEncoding;
            }
        }

        public bool HasDynamicLocalVariables
        {
            get
            {
                return _hasDynamicLocalVariables;
            }
        }

        public int MethodOrdinal
        {
            get
            {
                return _methodOrdinal;
            }
        }

        public ImmutableArray<LambdaDebugInfo> LambdaDebugInfo
        {
            get
            {
                return _lambdaDebugInfo;
            }
        }

        public ImmutableArray<ClosureDebugInfo> ClosureDebugInfo
        {
            get
            {
                return _closureDebugInfo;
            }
        }
    }
}
