using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace SparrowRunner.Gui
{
    public sealed class SourceScopeNode : INotifyPropertyChanged
    {
        private bool? _isChecked;
        private bool _isExpanded;
        private bool _updating;

        // initialChecked: 로컬 소스 범위(Track A/B)는 전체 선택으로 시작하고(기존 동작), xls 범위 트리(Track C)는
        // 아무것도 선택하지 않은 상태로 시작한다(미선택 = 전건 익스포트).
        public SourceScopeNode(string name, string fullPath, bool isFile, SourceScopeNode? parent = null,
                              bool initialChecked = true)
        {
            Name = name;
            FullPath = fullPath;
            IsFile = isFile;
            Parent = parent;
            Children = new ObservableCollection<SourceScopeNode>();
            _isChecked = initialChecked;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name { get; }
        public string FullPath { get; }
        public bool IsFile { get; }
        public bool HasChildren => Children.Count > 0;
        public SourceScopeNode? Parent { get; }
        public ObservableCollection<SourceScopeNode> Children { get; }

        /// <summary>Detections carried by this node (xls 범위 트리: 파일=그 파일의 검출 건수, 폴더=하위 합계).
        /// 0 이면 표시하지 않는다 — 로컬 소스 트리(Track A/B)는 건수 개념이 없어 기존과 동일하게 보인다.</summary>
        public int ItemCount { get; set; }

        /// <summary>트리에 보이는 문자열. 건수가 있으면 "이름 (N건)".</summary>
        public string Label => ItemCount > 0 ? Name + "  (" + ItemCount + "건)" : Name;

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                OnPropertyChanged(nameof(IsExpanded));
            }
        }

        public bool? IsChecked
        {
            get => _isChecked;
            set => SetChecked(value, updateChildren: true, updateParent: true);
        }

        public IEnumerable<string> EnumerateFiles()
        {
            if (IsFile)
            {
                yield return FullPath;
                yield break;
            }

            foreach (SourceScopeNode child in Children)
            {
                foreach (string file in child.EnumerateFiles())
                {
                    yield return file;
                }
            }
        }

        public IEnumerable<string> EnumerateSelectedFiles()
        {
            if (IsFile)
            {
                if (_isChecked == true) yield return FullPath;
                yield break;
            }

            foreach (SourceScopeNode child in Children)
            {
                foreach (string file in child.EnumerateSelectedFiles())
                {
                    yield return file;
                }
            }
        }

        public void SetSubtree(bool isChecked)
        {
            SetChecked(isChecked, updateChildren: true, updateParent: true);
        }

        public void ApplySelection(ISet<string> selectedFiles)
        {
            if (IsFile)
            {
                SetChecked(selectedFiles.Contains(FullPath), updateChildren: false, updateParent: true);
                return;
            }

            foreach (SourceScopeNode child in Children)
            {
                child.ApplySelection(selectedFiles);
            }
            RefreshFromChildren();
        }

        public void RefreshFromChildren()
        {
            if (IsFile || Children.Count == 0) return;
            bool all = Children.All(c => c.IsChecked == true);
            bool none = Children.All(c => c.IsChecked == false);
            SetChecked(all ? true : none ? false : null, updateChildren: false, updateParent: true);
        }

        private void SetChecked(bool? value, bool updateChildren, bool updateParent)
        {
            if (_updating) return;
            if (_isChecked == value && (!updateChildren || IsFile)) return;

            try
            {
                _updating = true;
                _isChecked = value;
                OnPropertyChanged(nameof(IsChecked));

                if (updateChildren && value.HasValue)
                {
                    foreach (SourceScopeNode child in Children)
                    {
                        child.SetChecked(value.Value, updateChildren: true, updateParent: false);
                    }
                }
            }
            finally
            {
                _updating = false;
            }

            if (updateParent)
            {
                Parent?.RefreshFromChildren();
            }
        }

        private void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
